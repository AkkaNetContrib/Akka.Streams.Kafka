using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Akka.Actor;
using Akka.Streams.Implementation;
using Akka.Streams.Kafka.Helpers;
using Akka.Streams.Kafka.Settings;
using Akka.Streams.Kafka.Stages.Consumers.Abstract;
using Akka.Streams.Kafka.Stages.Consumers.Actors;
using Akka.Streams.Stage;
using Akka.Streams.Supervision;
using Akka.Util.Internal;
using Confluent.Kafka;
using Decider = Akka.Streams.Supervision.Decider;
using Directive = Akka.Streams.Supervision.Directive;

namespace Akka.Streams.Kafka.Stages.Consumers
{
    internal class SingleSourceStageLogic<K, V, TMessage> : BaseSingleSourceLogic<K, V, TMessage>
    {
        private readonly SourceShape<TMessage> _shape;
        private readonly ConsumerSettings<K, V> _settings;
        private readonly ISubscription _subscription;

        private readonly Decider _decider;
        private readonly int _actorNumber = KafkaConsumerActorMetadata.NextNumber();

        private readonly TaskCompletionSource<NotUsed> _completion;
        private readonly CancellationTokenSource _cancellationTokenSource;

        public SingleSourceStageLogic(SourceShape<TMessage> shape, ConsumerSettings<K, V> settings, 
                                      ISubscription subscription, Attributes attributes, 
                                      TaskCompletionSource<NotUsed> completion, 
                                      Func<BaseSingleSourceLogic<K, V, TMessage>, IMessageBuilder<K, V, TMessage>> messageBuilderFactory) 
            : base(shape, completion, messageBuilderFactory)
        {
            _shape = shape;
            _settings = settings;
            _subscription = subscription;
            _completion = completion;
            _cancellationTokenSource = new CancellationTokenSource();

            var supervisionStrategy = attributes.GetAttribute<ActorAttributes.SupervisionStrategy>(null);
            _decider = supervisionStrategy != null ? supervisionStrategy.Decider : Deciders.ResumingDecider;
        }

        /// <inheritdoc />
        protected override void ConfigureSubscription()
        {
            switch (_subscription)
            {
                case TopicSubscription topicSubscription:
                    ConsumerActor.Tell(new KafkaConsumerActorMetadata.Internal.Subscribe(topicSubscription.Topics), SourceActor.Ref);
                    break;
                case Assignment assignment:
                    ConsumerActor.Tell(new KafkaConsumerActorMetadata.Internal.Assign(assignment.TopicPartitions), SourceActor.Ref);
                    break;
                case AssignmentWithOffset assignmentWithOffset:
                    ConsumerActor.Tell(new KafkaConsumerActorMetadata.Internal.AssignWithOffset(assignmentWithOffset.TopicPartitions), SourceActor.Ref);
                    break;
                case IManualSubscription manualSubscription:
                    ConfigureManualSubscription(manualSubscription);
                    break;
                default:
                    throw new NotSupportedException();
            }
        }

        /// <inheritdoc />
        protected override IActorRef CreateConsumerActor()
        {
            var partitionsAssignedHandler = GetAsyncCallback<IEnumerable<TopicPartition>>(PartitionsAssigned);
            var partitionsRevokedHandler = GetAsyncCallback<IEnumerable<TopicPartitionOffset>>(PartitionsRevoked);
            var errorOccuredHandler = GetAsyncCallback<Error>(HandleConsumeError);

            var eventHandler = new AsyncCallbacksConsumerEventHandler(errorOccuredHandler, partitionsAssignedHandler, partitionsRevokedHandler);
            
            if (!(Materializer is ActorMaterializer actorMaterializer))
                throw new ArgumentException($"Expected {typeof(ActorMaterializer)} but got {Materializer.GetType()}");
            
            var extendedActorSystem = actorMaterializer.System.AsInstanceOf<ExtendedActorSystem>();
            var actor = extendedActorSystem.SystemActorOf(KafkaConsumerActorMetadata.GetProps(SourceActor.Ref, _settings, eventHandler), 
                                                          $"kafka-consumer-{_actorNumber}");
            return actor;
        }

        public override void PostStop()
        {
            ConsumerActor.Tell(new KafkaConsumerActorMetadata.Internal.Stop(), SourceActor.Ref);

            base.PostStop();
        }

        protected override void PerformShutdown()
        {
            SetKeepGoing(true);
            
            if (!IsClosed(_shape.Outlet))
                Complete(_shape.Outlet);
            
            SourceActor.Become(ShuttingDownReceive);
            StopConsumerActor();
        }

        private void ShuttingDownReceive(Tuple<IActorRef, object> args)
        {
            switch (args.Item2)
            {
                case Terminated terminated when terminated.ActorRef.Equals(ConsumerActor):
                    OnShutdown();
                    CompleteStage();
                    break;
                default:
                    return;
            }
        }

        protected void StopConsumerActor()
        {
            Materializer.ScheduleOnce(_settings.StopTimeout, () =>
            {
                ConsumerActor.Tell(new KafkaConsumerActorMetadata.Internal.Stop(), SourceActor.Ref);
            });
        }

        private void PartitionsAssigned(IEnumerable<TopicPartition> partitions)
        {
            TopicPartitions = partitions.ToImmutableHashSet();
            Log.Debug($"Partitions were assigned: {string.Join(", ", TopicPartitions)}");
            RequestMessages();
        }
        
        private void PartitionsRevoked(IEnumerable<TopicPartitionOffset> partitions)
        {
            TopicPartitions = TopicPartitions.Clear();
            Log.Debug("Partitions were revoked");
        }
        
        private void HandleConsumeError(Error error)
        {
            Log.Error(error.Reason);
            // var exception = new SerializationException(error.Reason);
            var exception = new KafkaException(error);
            switch (_decider(exception))
            {
                case Directive.Stop:
                    // Throw
                    _completion.TrySetException(exception);
                    _cancellationTokenSource.Cancel();
                    FailStage(exception);
                    break;
                case Directive.Resume:
                    // keep going
                    break;
                case Directive.Restart:
                    // keep going
                    break;
            }
        }
        
        private void HandleError(Error error)
        {
            Log.Error(error.Reason);

            if (!KafkaExtensions.IsBrokerErrorRetriable(error) && !KafkaExtensions.IsLocalErrorRetriable(error))
            {
                var exception = new KafkaException(error);
                FailStage(exception);
            }
            else if (KafkaExtensions.IsLocalValueSerializationError(error))
            {
                var exception = new SerializationException(error.Reason);
                switch (_decider(exception))
                {
                    case Directive.Stop:
                        // Throw
                        _completion.TrySetException(exception);
                        FailStage(exception);
                        break;
                    case Directive.Resume:
                        // keep going
                        break;
                    case Directive.Restart:
                        // keep going
                        break;
                }
            }
        }
    }
}