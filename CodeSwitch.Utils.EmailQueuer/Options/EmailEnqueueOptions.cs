namespace CodeSwitch.Utils.EmailQueuer.Options
{
    /// <summary>
    /// Per-message options for <c>EnqueueAsync</c> (2.3.0). Every property is optional; a default
    /// instance queues exactly what the older overloads queue: normal priority, no partition.
    /// </summary>
    public class EmailEnqueueOptions
    {
        /// <summary>
        /// CC address or ';' separated list of addresses
        /// </summary>
        public string Cc { get; set; } = "";
        /// <summary>
        /// BCC address or ';' separated list of addresses
        /// </summary>
        public string Bcc { get; set; } = "";
        /// <summary>
        /// ';' separated list of file paths to attach
        /// </summary>
        public string AttachmentPaths { get; set; } = "";
        /// <summary>
        /// Send order. Higher goes first; 0 (the default) is normal mail. Use a negative value for
        /// bulk mail that should go out only when nothing of a higher priority is due.
        /// </summary>
        public int Priority { get; set; }
        /// <summary>
        /// Fairness key, opaque to the library (a tenant or customer id for instance), at most 64
        /// characters. Within one priority the worker takes turns between the distinct keys, so a
        /// burst under one key cannot hold up another key's mail. Mail is only ever reordered:
        /// never dropped, merged or rate limited. Null (the default) is a partition of its own.
        /// </summary>
        public string PartitionKey { get; set; }
    }
}
