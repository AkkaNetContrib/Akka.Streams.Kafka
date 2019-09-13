using System;
using System.Collections.Immutable;
using Akka.Annotations;
using Akka.Streams.Stage;
using Confluent.Kafka;

namespace Akka.Streams.Kafka.Helpers
{
    /// <summary>
    /// The API is new and may change in further releases.
    ///
    /// Allows to execute user code when Kafka rebalances partitions between consumers, or an Alpakka Kafka consumer is stopped.
    /// Use with care: These callbacks are called synchronously on the same thread Kafka's `poll()` is called.
    /// A warning will be logged if a callback takes longer than the configured `partition-handler-warning`.
    ///
    /// There is no point in calling `CommittableOffset`'s commit methods as their committing won't be executed as long as any of
    /// the callbacks in this class are called.
    /// </summary>
    [ApiMayChange]
    internal interface IPartitionEventHandler<K, V>
    {
        /// <summary>
        /// Called when partitions are revoked
        /// </summary>
        void OnRevoke(IImmutableSet<TopicPartitionOffset> revokedTopicPartitions, RestrictedConsumer<K, V> consumer);

        /// <summary>
        /// Called when partitions are assigned
        /// </summary>
        void OnAssign(IImmutableSet<TopicPartition> assignedTopicPartitions, RestrictedConsumer<K, V> consumer);

        /// <summary>
        /// Called when consuming is stopped
        /// </summary>
        void OnStop(IImmutableSet<TopicPartition> topicPartitions, RestrictedConsumer<K, V> consumer);
    }

    /// <summary>
    /// Dummy handler which does nothing. Also <see cref="IPartitionEventHandler{K,V}"/>
    /// </summary>
    internal class EmptyPartitionEventHandler<K, V> : IPartitionEventHandler<K, V>
    {
        /// <inheritdoc />
        public void OnRevoke(IImmutableSet<TopicPartitionOffset> revokedTopicPartitions, RestrictedConsumer<K, V> consumer)
        {
        }

        /// <inheritdoc />
        public void OnAssign(IImmutableSet<TopicPartition> assignedTopicPartitions, RestrictedConsumer<K, V> consumer)
        {
        }

        /// <inheritdoc />
        public void OnStop(IImmutableSet<TopicPartition> topicPartitions, RestrictedConsumer<K, V> consumer)
        {
        }
    }

    /// <summary>
    /// Handler allowing to pass custom stage callbacks. Also <see cref="IPartitionEventHandler{K,V}"/>
    /// </summary>
    internal class AsyncCallbacksPartitionEventHandler<K, V> : IPartitionEventHandler<K, V>
    {
        private readonly Action<IImmutableSet<TopicPartition>> _partitionAssignedCallback;
        private readonly Action<IImmutableSet<TopicPartitionOffset>> _partitionRevokedCallback;

        public AsyncCallbacksPartitionEventHandler(Action<IImmutableSet<TopicPartition>> partitionAssignedCallback,
                                                  Action<IImmutableSet<TopicPartitionOffset>> partitionRevokedCallback)
        {
            _partitionAssignedCallback = partitionAssignedCallback;
            _partitionRevokedCallback = partitionRevokedCallback;
        }

        /// <inheritdoc />
        public void OnRevoke(IImmutableSet<TopicPartitionOffset> revokedTopicPartitions, RestrictedConsumer<K, V> consumer)
        {
            _partitionRevokedCallback(revokedTopicPartitions);
        }

        /// <inheritdoc />
        public void OnAssign(IImmutableSet<TopicPartition> assignedTopicPartitions, RestrictedConsumer<K, V> consumer)
        {
            _partitionAssignedCallback(assignedTopicPartitions);
        }

        /// <inheritdoc />
        public void OnStop(IImmutableSet<TopicPartition> topicPartitions, RestrictedConsumer<K, V> consumer)
        {
        }
    }
}