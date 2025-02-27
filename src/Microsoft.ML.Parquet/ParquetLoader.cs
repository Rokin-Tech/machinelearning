﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Microsoft.ML;
using Microsoft.ML.CommandLine;
using Microsoft.ML.Data;
using Microsoft.ML.Data.IO;
using Microsoft.ML.Internal.Utilities;
using Microsoft.ML.Runtime;
using Parquet;
using Parquet.Data;
using Parquet.File.Values.Primitives;


[assembly: LoadableClass(ParquetLoader.Summary, typeof(ParquetLoader), typeof(ParquetLoader.Arguments), typeof(SignatureDataLoader),
    ParquetLoader.LoaderName, ParquetLoader.LoaderSignature, ParquetLoader.ShortName)]

[assembly: LoadableClass(ParquetLoader.Summary, typeof(ParquetLoader), null, typeof(SignatureLoadDataLoader),
    ParquetLoader.LoaderName, ParquetLoader.LoaderSignature)]

namespace Microsoft.ML.Data
{
    /// <summary>
    /// Loads a parquet file into an IDataView. Supports basic mapping from Parquet input column data types to framework data types.
    /// </summary>
    [BestFriend]
    internal sealed class ParquetLoader : ILegacyDataLoader, IDisposable
    {
        private sealed class ReaderOptions
        {
            public long Count;
            public long Offset;
            public string[] Columns;
        }
        /// <summary>
        /// A Column is a singular representation that consolidates all the related column chunks in the
        /// Parquet file. Information stored within the Column includes its name, raw type read from Parquet,
        /// its corresponding ColumnType, and index.
        /// Complex columns in Parquet like structs, maps, and lists are flattened into multiple columns.
        /// </summary>
        private sealed class Column
        {
            /// <summary>
            /// The name of the column.
            /// </summary>
            public readonly string Name;

            /// <summary>
            /// The column type of the column, translated from ParquetType.
            /// null when ParquetType is unsupported.
            /// </summary>
            public readonly DataViewType ColType;

            /// <summary>
            /// The DataField representation in the Parquet DataSet.
            /// </summary>
            public readonly DataField DataField;

            /// <summary>
            /// The DataType read from the Parquet file.
            /// </summary>
            public readonly DataType DataType;

            public Column(string name, DataViewType colType, DataField dataField, DataType dataType)
            {
                Contracts.AssertNonEmpty(name);
                Contracts.AssertValue(colType);
                Contracts.AssertValue(dataField);

                Name = name;
                ColType = colType;
                DataType = dataType;
                DataField = dataField;
            }
        }

        public sealed class Arguments
        {
            [Argument(ArgumentType.LastOccurrenceWins, HelpText = "Number of column chunk values to cache while reading from parquet file", ShortName = "chunkSize")]
            public int ColumnChunkReadSize = _defaultColumnChunkReadSize;

            [Argument(ArgumentType.LastOccurrenceWins, HelpText = "If true, will read large numbers as dates", ShortName = "bigIntDates")]
            public bool TreatBigIntegersAsDates = true;
        }

        internal const string Summary = "IDataView loader for Parquet files.";
        internal const string LoaderName = "Parquet Loader";
        internal const string LoaderSignature = "ParquetLoader";
        internal const string ShortName = "Parquet";
        internal const string ModelSignature = "PARQELDR";

        private const string SchemaCtxName = "Schema.idv";

        private readonly IHost _host;
        private readonly Stream _parquetStream;
        private readonly ParquetOptions _parquetOptions;
        private readonly int _columnChunkReadSize;
        private readonly Column[] _columnsLoaded;
        private const int _defaultColumnChunkReadSize = 1000000;

        private bool _disposed;
        private readonly long? _rowCount;

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: ModelSignature,
                //verWrittenCur: 0x00010001, // Initial
                verWrittenCur: 0x00010002, // Add Schema to Model Context
                verReadableCur: 0x00010002,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature,
                loaderAssemblyName: typeof(ParquetLoader).Assembly.FullName);
        }

        public ParquetLoader(IHostEnvironment env, Arguments args, IMultiStreamSource files)
            : this(env, args, OpenStream(files))
        {
        }

        public ParquetLoader(IHostEnvironment env, Arguments args, string filepath)
            : this(env, args, OpenStream(filepath))
        {
        }

        public ParquetLoader(IHostEnvironment env, Arguments args, Stream stream)
            : this(args, env.Register(LoaderSignature), stream)
        {
        }

        private ParquetLoader(Arguments args, IHost host, Stream stream)
        {
            Contracts.AssertValue(host, nameof(host));
            _host = host;

            _host.CheckValue(args, nameof(args));
            _host.CheckValue(stream, nameof(stream));
            _host.CheckParam(stream.CanRead, nameof(stream), "input stream must be readable");
            _host.CheckParam(stream.CanSeek, nameof(stream), "input stream must be seekable");
            _host.CheckParam(stream.Position == 0, nameof(stream), "input stream must be at head");

            using (var ch = _host.Start("Initializing host"))
            {
                _parquetStream = stream;
                _parquetOptions = new ParquetOptions()
                {
                    TreatByteArrayAsString = false,
                    TreatBigIntegersAsDates = args.TreatBigIntegersAsDates
                };

                DataField[] schemaDataSet = null;

                try
                {
                    // We only care about the schema so ignore the rows.
                    var pr = new Parquet.ParquetReader(stream, _parquetOptions);
                    schemaDataSet = pr.Schema.GetDataFields();
                    for (int rg = 0; rg < pr.RowGroupCount; rg++)
                    {
                        if (rg == 0) _rowCount = new long();
                        var groupReader = pr.OpenRowGroupReader(rg);
                        _rowCount += groupReader.RowCount;
                    }

                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("Cannot read Parquet file", ex);
                }

                _columnChunkReadSize = args.ColumnChunkReadSize;
                _columnsLoaded = InitColumns(schemaDataSet);
                Schema = CreateSchema(_host, _columnsLoaded);
            }
        }

        private ParquetLoader(IHost host, ModelLoadContext ctx, IMultiStreamSource files)
        {
            Contracts.AssertValue(host);
            _host = host;
            _host.AssertValue(ctx);
            _host.AssertValue(files);

            // *** Binary format ***
            // int: cached chunk size
            // bool: TreatBigIntegersAsDates flag
            // Schema of the loader (0x00010002)

            _columnChunkReadSize = ctx.Reader.ReadInt32();
            bool treatBigIntegersAsDates = ctx.Reader.ReadBoolean();

            if (ctx.Header.ModelVerWritten >= 0x00010002)
            {
                // Load the schema
                byte[] buffer = null;
                if (!ctx.TryLoadBinaryStream(SchemaCtxName, r => buffer = r.ReadByteArray()))
                    throw _host.ExceptDecode();
                var strm = new MemoryStream(buffer, writable: false);
                var loader = new BinaryLoader(_host, new BinaryLoader.Arguments(), strm);
                Schema = loader.Schema;
            }

            // Only load Parquest related data if a file is present. Otherwise, just the Schema is valid.
            if (files.Count > 0)
            {
                _parquetOptions = new ParquetOptions()
                {
                    TreatByteArrayAsString = true,
                    TreatBigIntegersAsDates = treatBigIntegersAsDates
                };

                _parquetStream = OpenStream(files);
                DataField[] schemaDataSet;

                try
                {
                    // We only care about the schema so ignore the rows.

                    var pr = new Parquet.ParquetReader(_parquetStream, _parquetOptions);
                    schemaDataSet = pr.Schema.GetDataFields();
                    for (int rg = 0; rg < pr.RowGroupCount; rg++)
                    {
                        if (rg == 0) _rowCount = new long();
                        var groupReader = pr.OpenRowGroupReader(rg);
                        _rowCount += groupReader.RowCount;
                    }

                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("Cannot read Parquet file", ex);
                }

                _columnsLoaded = InitColumns(schemaDataSet);
                Schema = CreateSchema(_host, _columnsLoaded);
            }
            else if (Schema == null)
            {
                throw _host.Except("Parquet loader must be created with one file");
            }
        }

        public static ParquetLoader Create(IHostEnvironment env, ModelLoadContext ctx, IMultiStreamSource files)
        {
            Contracts.CheckValue(env, nameof(env));
            IHost host = env.Register(LoaderName);

            env.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel(GetVersionInfo());
            env.CheckValue(files, nameof(files));

            return host.Apply("Loading Model",
                ch => new ParquetLoader(host, ctx, files));
        }

        /// <summary>
        /// Helper function called by the ParquetLoader constructor to initialize the Columns that belong in the Parquet file.
        /// Composite data fields are flattened; for example, a Map Field in Parquet is flattened into a Key column and a Value
        /// column.
        /// </summary>
        /// <param name="dataSet">The schema data set.</param>
        /// <returns>The array of flattened columns instantiated from the parquet file.</returns>
        private Column[] InitColumns(DataField[] dataSet)
        {
            List<Column> columnsLoaded = new List<Column>();

            foreach (var parquetField in dataSet)
            {
                FlattenFields(parquetField, ref columnsLoaded, false);
            }
            return columnsLoaded.ToArray();
        }

        private void FlattenFields(Field field, ref List<Column> cols, bool isRepeatable)
        {
            if (field is DataField df)
            {
                if (isRepeatable)
                {
                    cols.Add(new Column(df.Path, ConvertFieldType(DataType.Unspecified), df, DataType.Unspecified));
                }
                else
                {
                    cols.Add(new Column(df.Path, ConvertFieldType(df.DataType), df, df.DataType));
                }
            }
            else if (field is MapField mf)
            {
                var key = mf.Key;
                cols.Add(new Column(key.Path, ConvertFieldType(DataType.Unspecified), (DataField)key, DataType.Unspecified));

                var val = mf.Value;
                cols.Add(new Column(val.Path, ConvertFieldType(DataType.Unspecified), (DataField)val, DataType.Unspecified));
            }
            else if (field is StructField sf)
            {
                foreach (var structField in sf.Fields)
                {
                    FlattenFields(structField, ref cols, isRepeatable);
                }
            }
            else if (field is ListField lf)
            {
                FlattenFields(lf.Item, ref cols, true);
            }
            else
            {
                throw _host.ExceptNotSupp("Encountered unknown Parquet field type(Currently recognizes data, map, list, and struct).");
            }
        }

        /// <summary>
        /// Create a new schema from the given columns.
        /// </summary>
        /// <param name="ectx">The exception context.</param>
        /// <param name="cols">The columns.</param>
        /// <returns>The resulting schema.</returns>
        private DataViewSchema CreateSchema(IExceptionContext ectx, Column[] cols)
        {
            Contracts.AssertValue(ectx);
            Contracts.AssertValue(cols);
            var builder = new DataViewSchema.Builder();
            builder.AddColumns(cols.Select(c => new DataViewSchema.DetachedColumn(c.Name, c.ColType, null)));
            return builder.ToSchema();
        }

        /// <summary>
        /// Translates Parquet types to ColumnTypes.
        /// </summary>
        private DataViewType ConvertFieldType(DataType parquetType)
        {
            switch (parquetType)
            {
                case DataType.Boolean:
                    return BooleanDataViewType.Instance;
                case DataType.Byte:
                    return NumberDataViewType.Byte;
                case DataType.SignedByte:
                    return NumberDataViewType.SByte;
                case DataType.UnsignedByte:
                    return NumberDataViewType.Byte;
                case DataType.Short:
                    return NumberDataViewType.Int16;
                case DataType.UnsignedShort:
                    return NumberDataViewType.UInt16;
                case DataType.Int16:
                    return NumberDataViewType.Int16;
                case DataType.UnsignedInt16:
                    return NumberDataViewType.UInt16;
                case DataType.Int32:
                    return NumberDataViewType.Int32;
                case DataType.Int64:
                    return NumberDataViewType.Int64;
                case DataType.Int96:
                    return RowIdDataViewType.Instance;
                case DataType.ByteArray:
                    return new VectorDataViewType(NumberDataViewType.Byte);
                case DataType.String:
                    return TextDataViewType.Instance;
                case DataType.Float:
                    return NumberDataViewType.Single;
                case DataType.Double:
                    return NumberDataViewType.Double;
                case DataType.Decimal:
                    return NumberDataViewType.Double;
                case DataType.DateTimeOffset:
                    return DateTimeOffsetDataViewType.Instance;
                case DataType.Interval:
                    return TimeSpanDataViewType.Instance;
                default:
                    return TextDataViewType.Instance;
            }
        }

        private static Stream OpenStream(IMultiStreamSource files)
        {
            Contracts.CheckValue(files, nameof(files));
            Contracts.CheckParam(files.Count == 1, nameof(files), "Parquet loader must be created with one file");
            return files.Open(0);
        }

        private static Stream OpenStream(string filename)
        {
            Contracts.CheckNonEmpty(filename, nameof(filename));
            var files = new MultiFileSource(filename);
            return OpenStream(files);
        }

        public bool CanShuffle => true;

        public DataViewSchema Schema { get; }

        public long? GetRowCount()
        {
            return _rowCount;
        }

        public DataViewRowCursor GetRowCursor(IEnumerable<DataViewSchema.Column> columnsNeeded, Random rand = null)
        {
            _host.CheckValueOrNull(rand);
            return new Cursor(this, columnsNeeded, rand);
        }

        public DataViewRowCursor[] GetRowCursorSet(IEnumerable<DataViewSchema.Column> columnsNeeded, int n, Random rand = null)
        {
            _host.CheckValueOrNull(rand);
            return new DataViewRowCursor[] { GetRowCursor(columnsNeeded, rand) };
        }

        void ICanSaveModel.Save(ModelSaveContext ctx)
        {
            Contracts.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel();
            ctx.SetVersionInfo(GetVersionInfo());

            // *** Binary format ***
            // int: cached chunk size
            // bool: TreatBigIntegersAsDates flag
            // Schema of the loader

            ctx.Writer.Write(_columnChunkReadSize);
            ctx.Writer.Write(_parquetOptions.TreatBigIntegersAsDates);

            // Save the schema
            var noRows = new EmptyDataView(_host, Schema);
            var saverArgs = new BinarySaver.Arguments();
            saverArgs.Silent = true;
            var saver = new BinarySaver(_host, saverArgs);
            using (var strm = new MemoryStream())
            {
                var allColumns = Enumerable.Range(0, Schema.Count).ToArray();
                saver.SaveData(strm, noRows, allColumns);
                ctx.SaveBinaryStream(SchemaCtxName, w => w.WriteByteArray(strm.ToArray()));
            }
        }

        private sealed class Cursor : RootCursorBase
        {
            private readonly ParquetLoader _loader;
            private readonly Stream _fileStream;
            private readonly ParquetConversions _parquetConversions;
            private readonly ReaderOptions _readerOptions;
            private readonly int[] _actives;
            private readonly int[] _colToActivesIndex;
            private readonly Delegate[] _getters;
            private int _curDataSetRow;
            private IEnumerator<int> _dataSetEnumerator;
            private readonly IEnumerator<int> _blockEnumerator;
            private readonly IList[] _columnValues;
            private readonly Random _rand;

            public Cursor(ParquetLoader parent, IEnumerable<DataViewSchema.Column> columnsNeeded, Random rand)
               : base(parent._host)
            {
                Ch.AssertValue(columnsNeeded);
                Ch.AssertValue(parent._parquetStream);

                _loader = parent;
                _fileStream = parent._parquetStream;
                _parquetConversions = new ParquetConversions(Ch);
                _rand = rand;

                // Create Getter delegates
                Utils.BuildSubsetMaps(Schema.Count, columnsNeeded, out _actives, out _colToActivesIndex);
                _readerOptions = new ReaderOptions
                {
                    Count = _loader._columnChunkReadSize,
                    Columns = _loader._columnsLoaded.Select(i => i.Name).ToArray()
                };

                // The number of blocks is calculated based on the specified rows in a block (defaults to 1M).
                // Since we want to shuffle the blocks in addition to shuffling the rows in each block, checks
                // are put in place to ensure we can produce a shuffle order for the blocks.
                var numBlocks = MathUtils.DivisionCeiling((long)parent.GetRowCount(), _readerOptions.Count);
                if (numBlocks > int.MaxValue)
                {
                    throw _loader._host.ExceptParam(nameof(Arguments.ColumnChunkReadSize), "Error due to too many blocks. Try increasing block size.");
                }
                var blockOrder = CreateOrderSequence((int)numBlocks);
                _blockEnumerator = blockOrder.GetEnumerator();

                _dataSetEnumerator = Enumerable.Empty<int>().GetEnumerator();
                _columnValues = new IList[_actives.Length];
                _getters = new Delegate[_actives.Length];
                for (int i = 0; i < _actives.Length; ++i)
                {
                    int columnIndex = _actives[i];
                    _getters[i] = CreateGetterDelegate(columnIndex);
                }
            }

            #region CreateGetterDelegates
            private Delegate CreateGetterDelegate(int col)
            {
                Ch.CheckParam(IsColumnActive(Schema[col]), nameof(col));

                var parquetType = _loader._columnsLoaded[col].DataType;
                switch (parquetType)
                {
                    case DataType.Boolean:
                        return CreateGetterDelegateCore<bool, bool>(col, _parquetConversions.Conv);
                    case DataType.Byte:
                        return CreateGetterDelegateCore<byte, byte>(col, _parquetConversions.Conv);
                    case DataType.SignedByte:
                        return CreateGetterDelegateCore<sbyte?, sbyte>(col, _parquetConversions.Conv);
                    case DataType.UnsignedByte:
                        return CreateGetterDelegateCore<byte, byte>(col, _parquetConversions.Conv);
                    case DataType.Short:
                        return CreateGetterDelegateCore<short?, short>(col, _parquetConversions.Conv);
                    case DataType.UnsignedShort:
                        return CreateGetterDelegateCore<ushort, ushort>(col, _parquetConversions.Conv);
                    case DataType.Int16:
                        return CreateGetterDelegateCore<short?, short>(col, _parquetConversions.Conv);
                    case DataType.UnsignedInt16:
                        return CreateGetterDelegateCore<ushort, ushort>(col, _parquetConversions.Conv);
                    case DataType.Int32:
                        return CreateGetterDelegateCore<int?, int>(col, _parquetConversions.Conv);
                    case DataType.Int64:
                        return CreateGetterDelegateCore<long?, long>(col, _parquetConversions.Conv);
                    case DataType.Int96:
                        return CreateGetterDelegateCore<BigInteger, DataViewRowId>(col, _parquetConversions.Conv);
                    case DataType.ByteArray:
                        return CreateGetterDelegateCore<byte[], VBuffer<Byte>>(col, _parquetConversions.Conv);
                    case DataType.String:
                        return CreateGetterDelegateCore<string, ReadOnlyMemory<char>>(col, _parquetConversions.Conv);
                    case DataType.Float:
                        return CreateGetterDelegateCore<float?, Single>(col, _parquetConversions.Conv);
                    case DataType.Double:
                        return CreateGetterDelegateCore<double?, Double>(col, _parquetConversions.Conv);
                    case DataType.Decimal:
                        return CreateGetterDelegateCore<decimal?, Double>(col, _parquetConversions.Conv);
                    case DataType.DateTimeOffset:
                        return CreateGetterDelegateCore<DateTimeOffset, DateTimeOffset>(col, _parquetConversions.Conv);
                    case DataType.Interval:
                        return CreateGetterDelegateCore<Interval, TimeSpan>(col, _parquetConversions.Conv);
                    default:
                        return CreateGetterDelegateCore<IList, ReadOnlyMemory<char>>(col, _parquetConversions.Conv);
                }
            }

            private ValueGetter<TValue> CreateGetterDelegateCore<TSource, TValue>(int col, ValueMapper<TSource, TValue> valueConverter)
            {
                Ch.CheckParam(IsColumnActive(Schema[col]), nameof(col));
                Ch.CheckValue(valueConverter, nameof(valueConverter));

                int activeIdx = _colToActivesIndex[col];

                return (ref TValue value) =>
                {
                    Ch.Check(Position >= 0, RowCursorUtils.FetchValueStateError);
                    TSource val = (TSource)_columnValues[activeIdx][_curDataSetRow];
                    valueConverter(in val, ref value);
                };
            }
            #endregion

            private class DataSet
            {
                private readonly Dictionary<string, IList> _columns;
                private readonly Schema _schema;
                private readonly int _rowCount;

                public int RowCount => _rowCount;
                public IList GetColumn(DataField schemaElement, int offset = 0, int count = -1)
                {
                    return GetColumn(schemaElement.Path, offset, count);
                }

                internal IList GetColumn(string path, int offset, int count)
                {
                    if (!_columns.TryGetValue(path, out IList values))
                    {
                        return null;
                    }

                    //optimise for performance by not instantiating another list if you want all the column values
                    if (offset == 0 && count == -1) return values;

                    IList page = (IList)Activator.CreateInstance(values.GetType());
                    int max = (count == -1)
                       ? values.Count
                       : Math.Min(offset + count, values.Count);
                    for (int i = offset; i < max; i++)
                    {
                        page.Add(values[i]);
                    }
                    return page;
                }

                public long TotalRowCount { get; }

                public DataSet(Schema schema)
                {
                    _schema = schema ?? throw new ArgumentNullException(nameof(schema));

                    _columns = new Dictionary<string, IList>();
                }

                public DataSet(Schema schema, Dictionary<string, IList> pathToValues, long totalRowCount) : this(schema)
                {
                    _columns = pathToValues;
                    _rowCount = _columns.Count == 0 ? 0 : pathToValues.Min(pv => pv.Value.Count);
                    TotalRowCount = totalRowCount;

                }
            }

            protected override bool MoveNextCore()
            {


                if (_dataSetEnumerator.MoveNext())
                {
                    _curDataSetRow = _dataSetEnumerator.Current;
                    return true;
                }
                else if (_blockEnumerator.MoveNext())
                {
                    _readerOptions.Offset = (long)_blockEnumerator.Current * _readerOptions.Count;

                    // When current dataset runs out, read the next portion of the parquet file.
                    DataSet ds;
                    lock (_loader._parquetStream)
                    {
                        var pr = new Parquet.ParquetReader(_loader._parquetStream, _loader._parquetOptions);

                        var pathToValues = new Dictionary<string, IList>();
                        long pos = 0;
                        long rowsRead = 0;

                        for (int rg = 0; rg < pr.RowGroupCount; rg++)
                        {
                            var rgreader = pr.OpenRowGroupReader(rg);
                            if ((_readerOptions.Count != -1 && rowsRead >= _readerOptions.Count) ||
                                        (_readerOptions.Offset > pos + rgreader.RowCount - 1))
                            {
                                pos += rgreader.RowCount;
                                continue;
                            }
                            long offset = Math.Max(0, _readerOptions.Offset - pos);
                            long count = _readerOptions.Count == -1 ? rgreader.RowCount : Math.Min(_readerOptions.Count - rowsRead, rgreader.RowCount);


                            DataColumn[] datacolumns = pr.ReadEntireRowGroup(rg);

                            foreach (var datacolumn in datacolumns)
                            {
                                string path = datacolumn.Field.Path;
                                Type type = datacolumn.Data.GetType();
                                try
                                {
                                    IList chunkValues = new List<object>();

                                    for (int i = 0; i < count; i++)
                                    {
                                        var v = datacolumn.Data.GetValue(offset + i);
                                        chunkValues.Add(v);
                                    }

                                    if (!pathToValues.TryGetValue(path, out IList allValues))
                                    {
                                        pathToValues[path] = chunkValues;
                                    }
                                    else
                                    {
                                        foreach (object v in chunkValues)
                                        {
                                            allValues.Add(v);
                                        }
                                    }

                                    if (datacolumn == datacolumns[0]) // icol = 0
                                    {
                                        //todo: this may not work
                                        rowsRead += chunkValues.Count;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    throw new ParquetException($"fatal error reading column '{path}'", ex);
                                }
                            }

                            pos += rgreader.RowCount;
                        }
                        //Schema schema = pr.Schema;//
                        //schema = schema.Filter(_fieldPredicates);

                        ds = new DataSet(pr.Schema, pathToValues, pr.ThriftMetadata.Num_rows);
                    }

                    var dataSetOrder = CreateOrderSequence(ds.RowCount);
                    _dataSetEnumerator = dataSetOrder.GetEnumerator();
                    _curDataSetRow = dataSetOrder.ElementAt(0);

                    // Cache list for each active column
                    for (int i = 0; i < _actives.Length; i++)
                    {
                        Column col = _loader._columnsLoaded[_actives[i]];
                        _columnValues[i] = ds.GetColumn(col.DataField);
                    }

                    return _dataSetEnumerator.MoveNext();
                }
                return false;
            }


            public override DataViewSchema Schema => _loader.Schema;

            public override long Batch => 0;

            /// <summary>
            /// Returns a value getter delegate to fetch the value of column with the given columnIndex, from the row.
            /// This throws if the column is not active in this row, or if the type
            /// <typeparamref name="TValue"/> differs from this column's type.
            /// </summary>
            /// <typeparam name="TValue"> is the column's content type.</typeparam>
            /// <param name="column"> is the output column whose getter should be returned.</param>
            public override ValueGetter<TValue> GetGetter<TValue>(DataViewSchema.Column column)
            {
                Ch.CheckParam(IsColumnActive(column), nameof(column), "requested column not active");

                var originGetter = _getters[_colToActivesIndex[column.Index]];
                var getter = originGetter as ValueGetter<TValue>;
                if (getter == null)
                    throw Ch.Except($"Invalid TValue: '{typeof(TValue)}', " +
                            $"expected type: '{originGetter.GetType().GetGenericArguments().First()}'.");

                return getter;
            }

            public override ValueGetter<DataViewRowId> GetIdGetter()
            {
                return
                   (ref DataViewRowId val) =>
                   {
                       // Unique row id consists of Position of cursor (how many times MoveNext has been called), and position in file
                       Ch.Check(IsGood, RowCursorUtils.FetchValueStateError);
                       val = new DataViewRowId((ulong)(_readerOptions.Offset + _curDataSetRow), 0);
                   };
            }

            /// <summary>
            /// Returns whether the given column is active in this row.
            /// </summary>
            public override bool IsColumnActive(DataViewSchema.Column column)
            {
                Ch.CheckParam(column.Index < _colToActivesIndex.Length, nameof(column));
                return _colToActivesIndex[column.Index] >= 0;
            }

            /// <summary>
            /// Creates a in-order or shuffled sequence, based on whether _rand is specified.
            /// If unable to create a shuffle sequence, will default to sequential.
            /// </summary>
            /// <param name="size">Number of elements in the sequence.</param>
            /// <returns></returns>
            private IEnumerable<int> CreateOrderSequence(int size)
            {
                IEnumerable<int> order;
                try
                {
                    order = _rand == null ? Enumerable.Range(0, size) : Utils.GetRandomPermutation(_rand, size);
                }
                catch (OutOfMemoryException)
                {
                    order = Enumerable.Range(0, size);
                }
                return order;
            }
        }

        #region Dispose

        private void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _parquetStream.Dispose();
            }
            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ParquetLoader()
        {
            Dispose(false);
        }

        #endregion

        /// <summary>
        /// Contains conversion functions to convert Parquet type values to framework type values.
        /// </summary>
        private sealed class ParquetConversions
        {
            private readonly IChannel _ch;

            public ParquetConversions(IChannel channel)
            {
                _ch = channel;
            }

            public void Conv(in byte[] src, ref VBuffer<Byte> dst) => dst = src != null ? new VBuffer<byte>(src.Length, src) : new VBuffer<byte>(0, new byte[0]);

            public void Conv(in sbyte? src, ref sbyte dst) => dst = (sbyte)src;

            public void Conv(in byte src, ref byte dst) => dst = src;

            public void Conv(in short? src, ref short dst) => dst = (short)src;

            public void Conv(in ushort src, ref ushort dst) => dst = src;

            public void Conv(in int? src, ref int dst) => dst = (int)src;

            public void Conv(in long? src, ref long dst) => dst = (long)src;

            public void Conv(in float? src, ref Single dst) => dst = src ?? Single.NaN;

            public void Conv(in double? src, ref Double dst) => dst = src ?? Double.NaN;

            public void Conv(in decimal? src, ref Double dst) => dst = src != null ? Decimal.ToDouble((decimal)src) : Double.NaN;

            public void Conv(in string src, ref ReadOnlyMemory<char> dst) => dst = src.AsMemory();

            //Behavior for NA values is undefined.
            public void Conv(in bool src, ref bool dst) => dst = src;

            public void Conv(in DateTimeOffset src, ref DateTimeOffset dst) => dst = src;

            public void Conv(in IList src, ref ReadOnlyMemory<char> dst) => dst = ConvertListToString(src).AsMemory();

            /// <summary>
            ///  Converts a System.Numerics.BigInteger value to a RowId data type value.
            /// </summary>
            /// <param name="src">BigInteger value.</param>
            /// <param name="dst">RowId object.</param>
            public void Conv(in BigInteger src, ref DataViewRowId dst)
            {
                try
                {
                    byte[] arr = src.ToByteArray();
                    Array.Resize(ref arr, 16);
                    ulong lo = BitConverter.ToUInt64(arr, 0);
                    ulong hi = BitConverter.ToUInt64(arr, 8);
                    dst = new DataViewRowId(lo, hi);
                }
                catch (Exception ex)
                {
                    _ch.Error("Cannot convert BigInteger to RowId. Exception : '{0}'", ex.Message);
                    dst = default;
                }
            }

            /// <summary>
            /// Converts a Parquet Interval data type value to a TimeSpan data type value.
            /// </summary>
            /// <param name="src">Parquet Interval value (int : months, int : days, int : milliseconds).</param>
            /// <param name="dst">TimeSpan object.</param>
            public void Conv(in Interval src, ref TimeSpan dst)
            {
                dst = TimeSpan.FromDays(src.Months * 30 + src.Days) + TimeSpan.FromMilliseconds(src.Millis);
            }

            private string ConvertListToString(IList list)
            {
                if (list == null)
                {
                    return String.Empty;
                }

                StringBuilder sb = new StringBuilder();
                var enu = list.GetEnumerator();
                while (enu.MoveNext())
                {
                    if (enu.Current is IList && enu.Current.GetType().IsGenericType)
                    {
                        sb.Append("[" + ConvertListToString((IList)enu.Current) + "],");
                    }
                    else
                    {
                        sb.Append(enu.Current?.ToString() + ",");
                    }
                }

                if (sb.Length > 0)
                    sb.Remove(sb.Length - 1, 1);

                return sb.ToString();
            }
        }
    }
}
