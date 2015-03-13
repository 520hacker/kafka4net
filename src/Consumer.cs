﻿using System.Reactive.Concurrency;
using kafka4net.ConsumerImpl;
using kafka4net.Tracing;
using kafka4net.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;

namespace kafka4net
{
    public class Consumer : IDisposable
    {
        /// <summary>
        /// IMPORTANT!
        /// When subscribing to message flow, remember that messages are handled in kafks4net execution thread, which is designed to be async. This thread handles all internal driver callbacks.
        /// This way, if you handle messages with some delays or God forbids Wait/Result, than you will pause driver's ability to handle internal events.
        /// Please, when subscribe, use ObserveOn(your scheduler or sync context) to switch handle thread to something else. Or make sure that your handle routine is instant. How instant?
        /// For reference, driver thread is used to handle async socket data received event. This should give you an idea: for how log are you willing to delay network handling inside the driver.
        /// </summary>
        public readonly IObservable<ReceivedMessage> OnMessageArrived;

        internal readonly ISubject<ReceivedMessage> OnMessageArrivedInput = new Subject<ReceivedMessage>();

        private static readonly ILogger _log = Logger.GetLogger();

        internal ConsumerConfiguration Configuration { get; private set; }
        internal string Topic { get { return Configuration.Topic; } }

        private readonly Cluster _cluster;

        // Track map of partition ID to TopicPartition as well as partition ID to the IDisposable subscription for that TopicPartition.
        private readonly Dictionary<int, TopicPartition> _topicPartitions = new Dictionary<int, TopicPartition>(); 
        private readonly Dictionary<int, IDisposable> _partitionsSubscription = new Dictionary<int, IDisposable>();

        // called back when the connection is made for the consumer.
        readonly TaskCompletionSource<bool> _connectionComplete = new TaskCompletionSource<bool>();

        int _outstandingMessageProcessingCount;
        int _haveSubscriber;

        public bool FlowControlEnabled { get; private set; }

        readonly BehaviorSubject<int> _flowControlInput = new BehaviorSubject<int>(1);
        // Is used by Fetchers to wake up from sleep when flow turns ON
        internal IObservable<bool> FlowControl;


        /// <summary>
        /// Create a new consumer using the specified configuration. See @ConsumerConfiguration
        /// </summary>
        /// <param name="consumerConfig"></param>
        public Consumer(ConsumerConfiguration consumerConfig)
        {
            Configuration = consumerConfig;
            _cluster = new Cluster(consumerConfig.SeedBrokers);

            // Low/high watermark implementation
            FlowControl = _flowControlInput.
                Scan(1, (last, current) =>
                {
                    if (current < Configuration.LowWatermark)
                        return 1;
                    if (current > Configuration.HighWatermark)
                        return -1;
                    return last; // While in between watermarks, carry over previous on/off state
                }).
                    DistinctUntilChanged().
                    Select(i => i > 0).
                    Do(f =>FlowControlEnabled = f).
                    Do(f => _log.Debug("Flow control '{0}' on {1}", f ? "Open" : "Closed", this));

            var onMessage = Observable.Create<ReceivedMessage>(observer =>
            {
                if (Interlocked.CompareExchange(ref _haveSubscriber, 1, 0) == 1)
                    throw new InvalidOperationException("Only one subscriber is allowed. Use OnMessageArrived.Publish().RefCount()");

                // Relay messages from partition to consumer's output
                // Ensure that only single subscriber is allowed because it is important to count
                // outstanding messaged consumed by user
                OnMessageArrivedInput.Subscribe(observer);

                //
                // It is not possible to wait for completion of partition resolution process, so start it asynchronously.
                // This means that OnMessageArrived.Subscribe() will complete when consumer is not actually connected yet.
                //
                Task.Run(async () =>
                {
                    try 
                    {
                        await _cluster.ConnectAsync();
                        await SubscribeClient();
                        EtwTrace.Log.ConsumerStarted(GetHashCode(), Topic);
                        _connectionComplete.TrySetResult(true);

                        // check that we actually got any partitions subscribed
                        if (_topicPartitions.Count == 0)
                            OnMessageArrivedInput.OnCompleted();
                    }
                    catch (Exception e)
                    {
                        _connectionComplete.TrySetException(e);
                        OnMessageArrivedInput.OnError(e);
                    }
                });

                // upon unsubscribe
                return Disposable.Create(() => _partitionsSubscription.Values.ForEach(s=>s.Dispose()));
            });

            if (Configuration.UseFlowControl) { 
            // Increment counter of messages sent for processing
                onMessage = onMessage.Do(msg =>
                {
                    var count = Interlocked.Increment(ref _outstandingMessageProcessingCount);
                    _flowControlInput.OnNext(count);
                });
            }

            // handle stop condition
            onMessage = onMessage.Do(message =>
            {
                // check if this partition is done per the condition passed in configuration. If so, unsubscribe it.
                bool partitionDone = (Configuration.StopPosition.IsPartitionConsumingComplete(message));
                IDisposable partitionSubscription;
                if (partitionDone && _partitionsSubscription.TryGetValue(message.Partition, out partitionSubscription))
                {
                    _partitionsSubscription.Remove(message.Partition);
                    // calling Dispose here will cause the OnTopicPartitionComplete method to be called when it is completed.
                    partitionSubscription.Dispose();
                }
            });

            OnMessageArrived = onMessage;

            // If permanent error within any single partition, fail the whole consumer (intentionally). 
            // Do not try to keep going (failfast principle).
            _cluster.PartitionStateChanges.
                Where(s => s.ErrorCode.IsPermanentFailure()).
                Subscribe(state => OnMessageArrivedInput.OnError(new PartitionFailedException(state.Topic, state.PartitionId, state.ErrorCode)));
        }

        /// <summary>
        /// Start listening to partitions.
        /// </summary>
        /// <returns>Cleanup disposable which unsubscribes from partitions and clears the _topicPartitions</returns>
        async Task SubscribeClient()
        {
            await await _cluster.Scheduler.Ask(async () =>
            {
                // subscribe to all partitions
                var parts = await BuildTopicPartitionsAsync();
                parts.ForEach(part =>
                {
                    var partSubscription = part.Subscribe(this);
                    _partitionsSubscription.Add(part.PartitionId,partSubscription);
                });
                _log.Debug("Subscribed to partitions");
                return true;
            });
        }

        public void Ack(int messageCount = 1)
        {
            if(!Configuration.UseFlowControl)
                throw new InvalidOperationException("UseFlowControl is OFF, Ack is not allowed");

            var count = Interlocked.Add(ref _outstandingMessageProcessingCount, - messageCount);
            _flowControlInput.OnNext(count);
        }

        /// <summary>
        /// Pass connection method through from Cluster
        /// </summary>
        /// <returns></returns>
        public Task IsConnected 
        {
            get 
            {
                if(_haveSubscriber == 0)
                    throw new BrokerException("Can not connect because no subscription yet");
                return _connectionComplete.Task;
            }
        }

        /// <summary>
        ///  Provides access to the Cluster for this consumer
        /// </summary>
        public Cluster Cluster { get { return _cluster; } }

        /// <summary>For every patition, resolve offsets and build TopicPartition object</summary>
        private async Task<IEnumerable<TopicPartition>> BuildTopicPartitionsAsync()
        {
            // if they didn't specify explicit locations, initialize them here.
            var startPositionProvider = Configuration.StartPosition;
            if (startPositionProvider.StartLocation != ConsumerLocation.SpecifiedLocations)
            {
                // no offsets provided. Need to issue an offset request to get start/end locations and use them for consuming
                var partitions = await _cluster.FetchPartitionOffsetsAsync(Topic, startPositionProvider.StartLocation);

                if (_log.IsDebugEnabled)
                    _log.Debug("Consumer for topic {0} got time->offset resolved for location {1}. parts: [{2}]",
                        Topic, startPositionProvider,
                        string.Join(",", partitions.Partitions.OrderBy(p => p).Select(p => string.Format("{0}:{1}", p, partitions.NextOffset(p)))));

                IStartPositionProvider origProvider = startPositionProvider;
                // the new explicit offsets provider should use only the partitions included in the original one.
                startPositionProvider = new TopicPartitionOffsets(partitions.Topic, partitions.GetPartitionsOffset.Where(kv=> origProvider.ShouldConsumePartition(kv.Key)));
            }

            // we now have specified locations to start from, just get the partition metadata, and build the TopicPartitions
            var partitionMeta = await _cluster.GetOrFetchMetaForTopicAsync(Topic);
            return partitionMeta
                // only new partitions we don't already have in our dictionary
                .Where(pm => !_topicPartitions.ContainsKey(pm.Id))
                // only partitions we are "told" to.
                .Where(pm => startPositionProvider.ShouldConsumePartition(pm.Id))
                .Select(part =>
                {
                    var tp = new TopicPartition(_cluster, Topic, part.Id, startPositionProvider.GetStartOffset(part.Id));
                    _topicPartitions.Add(tp.PartitionId, tp);
                    return tp;
                });
        }

        internal void OnTopicPartitionComplete(TopicPartition topicPartition)
        {
            // called back from TopicPartition when its subscription is completed. 
            // Remove from our dictionary, and if all TopicPartitions are removed, call our own OnComplete.
            _topicPartitions.Remove(topicPartition.PartitionId);
            if (_topicPartitions.Count == 0)
            {
                // If we call OnCompleted right now, any last message that may be being sent will not process. Just tell the scheduler to do it in a moment.
                _cluster.Scheduler.Schedule(() => OnMessageArrivedInput.OnCompleted());
            }
        }


        public override string ToString()
        {
            return string.Format("Consumer: '{0}'", Topic);
        }

        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed)
                return;

            try
            {
                if (_partitionsSubscription != null)
                    _partitionsSubscription.Values.ForEach(s=>s.Dispose());
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch {}

            try
            {
                // close and release the connections in the Cluster.
                if (_cluster != null && _cluster.State != Cluster.ClusterState.Disconnected)
                    _cluster.CloseAsync(TimeSpan.FromSeconds(5)).
                        ContinueWith(t => _log.Error(t.Exception, "Error when closing Cluster"), TaskContinuationOptions.OnlyOnFaulted);
            }
            // ReSharper disable once EmptyGeneralCatchClause
            catch(Exception e)
            {
                _log.Error(e, "Error in Dispose");
            }

            _isDisposed = true;
        }
    }
}
