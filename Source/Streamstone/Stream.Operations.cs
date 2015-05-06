﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;

namespace Streamstone
{
    public sealed partial class Stream
    {
        class ProvisionOperation
        {
            readonly CloudTable table;
            readonly Stream stream;

            public ProvisionOperation(CloudTable table, Stream stream)
            {
                Requires.NotNull(table, "table");
                Requires.NotNull(stream, "stream");

                if (stream.IsPersistent)
                    throw new ArgumentException("Can't provision already persistent stream", "stream");

                this.table  = table;
                this.stream = stream;
            }

            public Stream Execute()
            {
                var insert = new Insert(stream);

                try
                {
                    table.Execute(insert.Prepare());
                }
                catch (StorageException e)
                {
                    insert.Handle(table, e);
                }

                return insert.Result();
            }

            public async Task<Stream> ExecuteAsync()
            {
                var insert = new Insert(stream);

                try
                {
                    await table.ExecuteAsync(insert.Prepare()).Really();
                }
                catch (StorageException e)
                {
                    insert.Handle(table, e);
                }

                return insert.Result();
            }

            class Insert
            {
                readonly StreamEntity stream;
                readonly Partition partition;

                public Insert(Stream stream)
                {
                    this.stream = stream.Entity();
                    partition = stream.Partition;
                }

                public TableOperation Prepare()
                {
                    return TableOperation.Insert(stream);
                }

                internal void Handle(CloudTable table, StorageException exception)
                {
                    if (exception.RequestInformation.HttpStatusCode == (int)HttpStatusCode.Conflict)
                        throw ConcurrencyConflictException.StreamChangedOrExists(table, partition);

                    throw exception.PreserveStackTrace();
                }

                internal Stream Result()
                {
                    return From(partition, stream);
                }
            }
        }

        class WriteOperation
        {
            readonly CloudTable table;
            readonly Stream stream;
            readonly Event[] events;
            readonly Include[] includes;

            public WriteOperation(CloudTable table, Stream stream, Event[] events, Include[] includes)
            {
                Requires.NotNull(table, "table");
                Requires.NotNull(stream, "stream");
                Requires.NotNull(events, "events");
                Requires.NotNull(events, "includes");

                if (events.Length == 0)
                    throw new ArgumentOutOfRangeException("events", "Events have 0 items");

                const int maxBatchSize = 100;
                const int entitiesPerEvent = 2;
                const int streamEntityPerBatch = 1;
                const int maxEventsPerBatch = (maxBatchSize / entitiesPerEvent) - streamEntityPerBatch;
                const int maxEntitiesTotalPerBatch = maxBatchSize - streamEntityPerBatch;

                if (events.Length > maxEventsPerBatch)
                    throw new ArgumentOutOfRangeException("events",
                        "Maximum number of events per batch is " + maxEventsPerBatch);

                if (events.Length * 2 + includes.Length > maxEntitiesTotalPerBatch)
                    throw new ArgumentOutOfRangeException("includes",
                        "Maximum number of includes you can put in this batch is " + 
                            (maxEntitiesTotalPerBatch - events.Length * 2));

                this.table = table;
                this.stream = stream;
                this.events = events;
                this.includes = includes;
            }

            public StreamWriteResult Execute()
            {
                var batch = new Batch(stream, events, includes);

                try
                {
                    table.ExecuteBatch(batch.Prepare());
                }
                catch (StorageException e)
                {
                    batch.Handle(table, e);
                }

                return batch.Result();
            }

            public async Task<StreamWriteResult> ExecuteAsync()
            {
                var batch = new Batch(stream, events, includes);

                try
                {
                    await table.ExecuteBatchAsync(batch.Prepare()).Really();
                }
                catch (StorageException e)
                {
                    batch.Handle(table, e);
                }

                return batch.Result();
            }

            class Batch
            {
                readonly TableBatchOperation batch = new TableBatchOperation();
                readonly List<ITableEntity> items = new List<ITableEntity>();

                readonly StreamEntity stream;
                readonly Partition partition;

                readonly RecordedEvent[] events;
                readonly Include[] includes;

                internal Batch(Stream stream, ICollection<Event> events, Include[] includes)
                {                 
                    this.stream = stream.Entity();
                    this.stream.Version = stream.Version + events.Count;
                    this.partition = stream.Partition;

                    this.events = events
                        .Select((e, i) => e.Record(stream.Version + i + 1))
                        .ToArray();

                    this.includes = includes;
                }

                internal TableBatchOperation Prepare()
                {
                    WriteStream();
                    WriteEvents();
                    WriteIncludes();

                    return batch;
                }

                void WriteStream()
                {
                    if (stream.ETag == null)
                        batch.Insert(stream);
                    else
                        batch.Replace(stream);

                    items.Add(stream);
                }

                void WriteEvents()
                {
                    foreach (var e in events)
                    {
                        var eventEntity = e.EventEntity(partition);
                        var eventIdEntity = e.IdEntity(partition);

                        batch.Insert(eventEntity);
                        batch.Insert(eventIdEntity);

                        items.Add(eventEntity);
                        items.Add(eventIdEntity);
                    }
                }

                void WriteIncludes()
                {
                    foreach (var include in includes)
                    {
                        batch.Add(include.Apply(partition, stream.Version));
                        items.Add(include.Entity);
                    }
                }

                internal StreamWriteResult Result()
                {
                    return new StreamWriteResult(From(partition, stream), events);
                }

                internal void Handle(CloudTable table, StorageException exception)
                {
                    if (exception.RequestInformation.HttpStatusCode == (int) HttpStatusCode.PreconditionFailed)
                        throw ConcurrencyConflictException.StreamChangedOrExists(table, partition);

                    if (exception.RequestInformation.HttpStatusCode != (int) HttpStatusCode.Conflict)
                        throw exception.PreserveStackTrace();

                    var error = exception.RequestInformation.ExtendedErrorInformation;
                    if (error.ErrorCode != "EntityAlreadyExists")
                        throw UnexpectedStorageResponseException.ErrorCodeShouldBeEntityAlreadyExists(error);

                    var position = ParseConflictingEntityPosition(error);
                    Debug.Assert(position >= 0 && position < items.Count);

                    var conflicting = items[position];
                    if (conflicting is EventIdEntity)
                    {
                        var duplicate = events[(position - 1) / 2];
                        throw new DuplicateEventException(table, partition, duplicate.Id);
                    }

                    var @event = conflicting as EventEntity;
                    if (@event != null)
                        throw ConcurrencyConflictException.EventVersionExists(table, partition, @event.Version);

                    var include = Array.Find(includes, x => x.Entity == conflicting);
                    if (include != null)
                        throw new IncludedOperationConflictException(table, partition, include);

                    throw new WarningException("How did this happen? We've got conflict on entity which is neither event nor id or include");
                }

                static int ParseConflictingEntityPosition(StorageExtendedErrorInformation error)
                {
                    var lines = error.ErrorMessage.Split('\n');
                    if (lines.Length != 3)
                        throw UnexpectedStorageResponseException.ConflictExceptionMessageShouldHaveExactlyThreeLines(error);

                    var semicolonIndex = lines[0].IndexOf(":", StringComparison.Ordinal);
                    if (semicolonIndex == -1)
                        throw UnexpectedStorageResponseException.ConflictExceptionMessageShouldHaveSemicolonOnFirstLine(error);

                    int position;
                    if (!int.TryParse(lines[0].Substring(0, semicolonIndex), out position))
                        throw UnexpectedStorageResponseException.UnableToParseTextBeforeSemicolonToInteger(error);

                    return position;
                }
            }
        }

        class SetPropertiesOperation
        {
            readonly CloudTable table;
            readonly Stream stream;
            readonly StreamProperties properties;

            public SetPropertiesOperation(CloudTable table, Stream stream, StreamProperties properties)
            {
                Requires.NotNull(table, "table");
                Requires.NotNull(stream, "stream");
                Requires.NotNull(properties, "properties");

                if (stream.IsTransient)
                    throw new ArgumentException("Can't set properties on transient stream", "stream");

                this.table = table;
                this.stream = stream;
                this.properties = properties;                
            }

            public Stream Execute()
            {
                var replace = new Replace(stream, properties);

                try
                {
                    table.Execute(replace.Prepare());
                }
                catch (StorageException e)
                {
                    replace.Handle(table, e);
                }

                return replace.Result();
            }

            public async Task<Stream> ExecuteAsync()
            {
                var replace = new Replace(stream, properties);

                try
                {
                    await table.ExecuteAsync(replace.Prepare()).Really();
                }
                catch (StorageException e)
                {
                    replace.Handle(table, e);
                }

                return replace.Result();
            }

            class Replace
            {
                readonly StreamEntity stream;
                readonly Partition partition;

                public Replace(Stream stream, StreamProperties properties)
                {
                    this.stream = stream.Entity();
                    this.stream.Properties = properties;
                    partition = stream.Partition;
                }

                internal TableOperation Prepare()
                {                    
                    return TableOperation.Replace(stream);
                }

                internal void Handle(CloudTable table, StorageException exception)
                {
                    if (exception.RequestInformation.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed)
                        throw ConcurrencyConflictException.StreamChanged(table, partition);

                    throw exception.PreserveStackTrace();
                }

                internal Stream Result()
                {
                    return From(partition, stream);
                }
            }
        }

        class OpenStreamOperation
        {
            readonly CloudTable table;
            readonly Partition partition;

            public OpenStreamOperation(CloudTable table, Partition partition)
            {
                Requires.NotNull(table, "table");
                Requires.NotNull(partition, "partition");

                this.table = table;
                this.partition = partition;
            }

            public StreamOpenResult Execute()
            {
                return Result(table.Execute(Prepare()));
            }

            public async Task<StreamOpenResult> ExecuteAsync()
            {
                return Result(await table.ExecuteAsync(Prepare()));
            }

            TableOperation Prepare()
            {
                return TableOperation.Retrieve<StreamEntity>(partition.PartitionKey, partition.StreamRowKey());
            }

            StreamOpenResult Result(TableResult result)
            {
                var entity = result.Result;

                return entity != null
                           ? new StreamOpenResult(true, From(partition, (StreamEntity)entity))
                           : StreamOpenResult.NotFound;
            }
        }

        class ReadOperation<T> where T : class, new()
        {
            readonly CloudTable table;
            readonly Partition partition;
            readonly int startVersion;
            readonly int sliceSize;

            public ReadOperation(CloudTable table, Partition partition, int startVersion, int sliceSize)
            {
                Requires.NotNull(table, "table");
                Requires.NotNull(partition, "partition");
                Requires.GreaterThanOrEqualToOne(startVersion, "startVersion");
                Requires.GreaterThanOrEqualToOne(sliceSize, "sliceSize");

                this.table = table;
                this.partition = partition;
                this.startVersion = startVersion;
                this.sliceSize = sliceSize;
            }

            public StreamSlice<T> Execute()
            {
                return Result(ExecuteQuery(PrepareQuery()));
            }

            public async Task<StreamSlice<T>> ExecuteAsync()
            {
                return Result(await ExecuteQueryAsync(PrepareQuery()));
            }

            StreamSlice<T> Result(ICollection<DynamicTableEntity> entities)
            {
                var streamEntity = FindStreamEntity(entities);
                entities.Remove(streamEntity);

                var stream = BuildStream(streamEntity);
                var events = BuildEvents(entities);

                return new StreamSlice<T>(stream, events, startVersion, sliceSize);
            }

            TableQuery<DynamicTableEntity> PrepareQuery()
            {
                var rowKeyStart = partition.EventVersionRowKey(startVersion);
                var rowKeyEnd = partition.EventVersionRowKey(startVersion + sliceSize - 1);

                // ReSharper disable StringCompareToIsCultureSpecific

                var query = table
                    .CreateQuery<DynamicTableEntity>()
                    .Where(x =>
                           x.PartitionKey == partition.PartitionKey
                           && (x.RowKey == partition.StreamRowKey()
                               || (x.RowKey.CompareTo(rowKeyStart)  >= 0
                                   && x.RowKey.CompareTo(rowKeyEnd) <= 0)));

                return (TableQuery<DynamicTableEntity>) query;
            }

            List<DynamicTableEntity> ExecuteQuery(TableQuery<DynamicTableEntity> query)
            {
                var result = new List<DynamicTableEntity>();
                TableContinuationToken token = null;

                do
                {
                    var segment = table.ExecuteQuerySegmented(query, token);
                    token = segment.ContinuationToken;
                    result.AddRange(segment.Results);
                }
                while (token != null);

                return result;
            }

            async Task<List<DynamicTableEntity>> ExecuteQueryAsync(TableQuery<DynamicTableEntity> query)
            {
                var result = new List<DynamicTableEntity>();
                TableContinuationToken token = null;

                do
                {
                    var segment = await table.ExecuteQuerySegmentedAsync(query, token).Really();
                    token = segment.ContinuationToken;
                    result.AddRange(segment.Results);
                }
                while (token != null);

                return result;
            }

            DynamicTableEntity FindStreamEntity(IEnumerable<DynamicTableEntity> entities)
            {
                return entities.Single(x => x.RowKey == partition.StreamRowKey());
            }

            Stream BuildStream(DynamicTableEntity entity)
            {
                return From(partition, StreamEntity.From(entity));
            }

            static T[] BuildEvents(IEnumerable<DynamicTableEntity> entities)
            {
                return entities.Select(e => e.Properties).Select(properties =>
                {
                    var t = new T();
                    TableEntity.ReadUserObject(t, properties, new OperationContext());
                    return t;
                })
                .ToArray();
            }
        }
    }
}
