﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Akka.Streams.Kafka.Settings;
using Akka.Streams.Stage;
using Confluent.Kafka;

namespace Akka.Streams.Kafka.Stages
{
    internal class KafkaSourceStage<K, V, Msg> : GraphStageWithMaterializedValue<SourceShape<Msg>, Task>
    {
        private readonly ConsumerSettings<K, V> _settings;
        private readonly ISubscription _subscription;

        protected readonly Outlet<Msg> Out = new Outlet<Msg>("kafka.consumer.out");
        public override SourceShape<Msg> Shape { get; }

        public KafkaSourceStage(ConsumerSettings<K, V> settings, ISubscription subscription)
        {
            _settings = settings;
            _subscription = subscription;
            Shape = new SourceShape<Msg>(Out);
        }

        public override ILogicAndMaterializedValue<Task> CreateLogicAndMaterializedValue(Attributes inheritedAttributes)
        {
            return new LogicAndMaterializedValue<Task>(new KafkaSourceStage<K, V>(_settings, _subscription, Shape), Task.CompletedTask);
        }
    }

    internal class KafkaSourceStage<K, V> : TimerGraphStageLogic
    {
        private readonly ConsumerSettings<K, V> _settings;
        private readonly ISubscription _subscription;
        private readonly Outlet _out;
        private Consumer<K, V> _consumer;

        private Action<Message<K, V>> _messagesReceived;
        private Action<IEnumerable<TopicPartition>> _partitionsAssigned;
        private Action<IEnumerable<TopicPartition>> _partitionsRevoked;

        private const string TimerKey = "PollTimer";

        private readonly Queue<Message<K, V>> _buffer = new Queue<Message<K, V>>();

        public KafkaSourceStage(ConsumerSettings<K, V> settings, ISubscription subscription, Shape shape) : base(shape)
        {
            _settings = settings;
            _subscription = subscription;
            _out = shape.Outlets.FirstOrDefault();

            SetHandler(_out, onPull:() =>
            {
                if (_buffer.Count > 0)
                {
                    Push(_out, _buffer.Dequeue());
                }
                else
                {
                    PullQueue();
                }
            });
        }

        public override void PreStart()
        {
            base.PreStart();

            _consumer = _settings.CreateKafkaConsumer();
            Log.Debug($"Consumer started: {_consumer.Name}");

            _consumer.OnMessage += HandleOnMessage;
            _consumer.OnConsumeError += HandleConsumeError;
            _consumer.OnError += HandleOnError;
            _consumer.OnPartitionsAssigned += HandleOnPartitionsAssigned;
            _consumer.OnPartitionsRevoked += HandleOnPartitionsRevoked;

            switch (_subscription)
            {
                case TopicSubscription ts:
                    _consumer.Subscribe(ts.Topics);
                    break;
                case Assignment a:
                    _consumer.Assign(a.TopicPartitions);
                    break;
                case AssignmentWithOffset awo:
                    _consumer.Assign(awo.TopicPartitions);
                    break;
            }

            _messagesReceived = GetAsyncCallback<Message<K, V>>(MessagesReceived);
            _partitionsAssigned = GetAsyncCallback<IEnumerable<TopicPartition>>(PartitionsAssigned);
            _partitionsRevoked = GetAsyncCallback<IEnumerable<TopicPartition>>(PartitionsRevoked);
        }

        public override void PostStop()
        {
            _consumer.OnMessage -= HandleOnMessage;
            _consumer.OnConsumeError -= HandleConsumeError;
            _consumer.OnError -= HandleOnError;
            _consumer.OnPartitionsAssigned -= HandleOnPartitionsAssigned;
            _consumer.OnPartitionsRevoked -= HandleOnPartitionsRevoked;

            Log.Debug($"Consumer stopped {_consumer.Name}");
            _consumer.Dispose();

            base.PostStop();
        }

        //
        // Consumer's events
        //

        private void HandleOnMessage(object sender, Message<K, V> message) => _messagesReceived.Invoke(message);

        private void HandleConsumeError(object sender, Message message)
        {
            Log.Error(message.Error.Reason);
        }

        private void HandleOnError(object sender, Error error)
        {
            Log.Error(error.Reason);

            if (!KafkaExtensions.IsBrokerErrorRetriable(error) && !KafkaExtensions.IsLocalErrorRetriable(error))
            {
                FailStage(new Exception(error.Reason));
            }
        }

        private void HandleOnPartitionsAssigned(object sender, List<TopicPartition> list)
        {
            _partitionsAssigned.Invoke(list);
        }

        private void HandleOnPartitionsRevoked(object sender, List<TopicPartition> list)
        {
            _partitionsRevoked.Invoke(list);
        }

        //
        // Async callbacks
        //

        private void MessagesReceived(Message<K, V> message)
        {
            _buffer.Enqueue(message);
            if (IsAvailable(_out))
            {
                Push(_out, _buffer.Dequeue());
            }
        }

        private void PartitionsAssigned(IEnumerable<TopicPartition> partitions)
        {
            Log.Debug($"Partitions were assigned: {_consumer.Name}");
            _consumer.Assign(partitions);
        }

        private void PartitionsRevoked(IEnumerable<TopicPartition> partitions)
        {
            Log.Debug($"Partitions were revoked: {_consumer.Name}");
            _consumer.Unassign();
        }

        private void PullQueue()
        {
            // TODO: should I call `Poll` if there are no assignments? Like in `Subscribe` flow
            _consumer.Poll(_settings.PollTimeout);

            if (_buffer.Count == 0)
                ScheduleOnce(TimerKey, _settings.PollInterval);
        }

        protected override void OnTimer(object timerKey) => PullQueue();
    }
}
