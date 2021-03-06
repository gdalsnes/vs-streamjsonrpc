// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

// modified WebSocketMessageHandler from StreamJsonRpc, added compression
namespace StreamJsonRpc
{
    using System;
    using System.Buffers;
    using System.IO.Compression;
    using System.Net.WebSockets;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft;
    using Nerdbank.Streams;
    using StreamJsonRpc.Protocol;
    using StreamJsonRpc.Reflection;

    /// <summary>
    /// A message handler for the <see cref="JsonRpc"/> class
    /// that uses <see cref="System.Net.WebSockets.WebSocket"/> as the transport.
    /// </summary>
    public class WebSocketMessageHandlerGZip : MessageHandlerBase
    {
        private readonly int sizeHint;

        private readonly bool compress;

        private DateTime lastSend = DateTime.UtcNow;
        private DateTime lastReceive = DateTime.UtcNow;

        public DateTime LastSend => this.lastSend;
        public DateTime LastReceive => this.lastReceive;

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketMessageHandler"/> class
        /// that uses the <see cref="JsonMessageFormatter"/> to serialize messages as textual JSON.
        /// </summary>
        /// <param name="webSocket">
        /// The <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.
        /// This will <em>not</em> be automatically disposed of with this <see cref="WebSocketMessageHandler"/>.
        /// </param>
        public WebSocketMessageHandlerGZip(WebSocket webSocket, bool compress)
            : this(webSocket, new JsonMessageFormatter(), compress)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebSocketMessageHandler"/> class.
        /// </summary>
        /// <param name="webSocket">
        /// The <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.
        /// This will <em>not</em> be automatically disposed of with this <see cref="WebSocketMessageHandler"/>.
        /// </param>
        /// <param name="formatter">The formatter to use to serialize <see cref="JsonRpcMessage"/> instances.</param>
        /// <param name="sizeHint">
        /// The size of the buffer to use for reading JSON-RPC messages.
        /// Messages which exceed this size will be handled properly but may require multiple I/O operations.
        /// </param>
        public WebSocketMessageHandlerGZip(WebSocket webSocket, IJsonRpcMessageFormatter formatter, bool compress, int sizeHint = 4096)
            : base(formatter)
        {
            Requires.NotNull(webSocket, nameof(webSocket));
            Requires.Range(sizeHint > 0, nameof(sizeHint));

            this.WebSocket = webSocket;
            this.sizeHint = sizeHint;
            this.compress = compress;
        }

        /// <inheritdoc />
        public override bool CanWrite => true;

        /// <inheritdoc />
        public override bool CanRead => true;

        /// <summary>
        /// Gets the <see cref="System.Net.WebSockets.WebSocket"/> used to communicate.
        /// </summary>
        public WebSocket WebSocket { get; }


        /// <inheritdoc />
        protected override async ValueTask<JsonRpcMessage> ReadCoreAsync(CancellationToken cancellationToken)
        {
            using (var contentSequenceBuilder = new Sequence<byte>())
            {
#if NETCOREAPP2_1
                ValueWebSocketReceiveResult result;
#else
                WebSocketReceiveResult result;
#endif
                do
                {
                    Memory<byte> memory = contentSequenceBuilder.GetMemory(this.sizeHint);
#if NETCOREAPP2_1
                    result = await this.WebSocket.ReceiveAsync(memory, cancellationToken).ConfigureAwait(false);
					this.lastReceive = DateTime.UtcNow;
                    contentSequenceBuilder.Advance(result.Count);
#else
                    ArrayPool<byte> pool = ArrayPool<byte>.Shared;
                    byte[] segment = pool.Rent(this.sizeHint);
                    try
                    {
                        result = await this.WebSocket.ReceiveAsync(new ArraySegment<byte>(segment), cancellationToken).ConfigureAwait(false);
                        this.lastReceive = DateTime.UtcNow;
                        contentSequenceBuilder.Write(segment.AsSpan(0, result.Count));
                    }
                    finally
                    {
                        pool.Return(segment);
                    }
#endif
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // These are the only valid states for calling CloseAsync
                        switch (this.WebSocket.State)
                        {
                            case WebSocketState.Open:
                            case WebSocketState.CloseReceived:
                            case WebSocketState.CloseSent:
                                await this.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed as requested.", CancellationToken.None).ConfigureAwait(false);
                                break;
                        }

                        return null;
                    }
                }
                while (!result.EndOfMessage);

                if (this.compress)
                {
                    if (contentSequenceBuilder.AsReadOnlySequence.Length > 0)
                    {
                        using (var contentSequenceBuilder2 = new Sequence<byte>())
                        {
                            using (var gzipStream = new GZipStream(contentSequenceBuilder.AsReadOnlySequence.AsStream(), CompressionMode.Decompress))
                            {
                                gzipStream.CopyTo(contentSequenceBuilder2.AsStream());
                            }

                            return this.Formatter.Deserialize(contentSequenceBuilder2);
                        }
                    }
                    else
                    {
                        return null;
                    }
                }

                return contentSequenceBuilder.AsReadOnlySequence.Length > 0 ? this.Formatter.Deserialize(contentSequenceBuilder) : null;
            }
        }

        /// <inheritdoc />
        protected override async ValueTask WriteCoreAsync(JsonRpcMessage content, CancellationToken cancellationToken)
        {
            Requires.NotNull(content, nameof(content));

            using (var contentSequenceBuilder = new Sequence<byte>())
            {
                WebSocketMessageType messageType = this.Formatter is IJsonRpcMessageTextFormatter ? WebSocketMessageType.Text : WebSocketMessageType.Binary;
                this.Formatter.Serialize(contentSequenceBuilder, content);
                cancellationToken.ThrowIfCancellationRequested();

                // Some formatters (e.g. MessagePackFormatter) needs the encoded form in order to produce JSON for tracing.
                // Other formatters (e.g. JsonMessageFormatter) would prefer to do its own tracing while it still has a JToken.
                // We only help the formatters that need the byte-encoded form here. The rest can do it themselves.
                if (this.Formatter is IJsonRpcFormatterTracingCallbacks tracer)
                {
                    tracer.OnSerializationComplete(content, contentSequenceBuilder);
                }

                if (this.compress)
                {
                    using (var contentSequenceBuilder2 = new Sequence<byte>())
                    {
                        using (var gzipStream = new GZipStream(contentSequenceBuilder2.AsStream(), CompressionLevel.Fastest))
                        {
                            contentSequenceBuilder.AsReadOnlySequence.AsStream().CopyTo(gzipStream);
                        }

                        messageType = WebSocketMessageType.Binary;
                        await this.SendAsync(contentSequenceBuilder2, messageType, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await this.SendAsync(contentSequenceBuilder, messageType, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task SendAsync(Sequence<byte> contentSequenceBuilder, WebSocketMessageType messageType, CancellationToken cancellationToken)
        {
            int bytesCopied = 0;
            ReadOnlySequence<byte> contentSequence = contentSequenceBuilder.AsReadOnlySequence;
            foreach (ReadOnlyMemory<byte> memory in contentSequence)
            {
                bool endOfMessage = bytesCopied + memory.Length == contentSequence.Length;
#if NETCOREAPP2_1
                await this.WebSocket.SendAsync(memory, messageType, endOfMessage, cancellationToken).ConfigureAwait(false);
				this.lastSend = DateTime.UtcNow;
#else
                if (MemoryMarshal.TryGetArray(memory, out ArraySegment<byte> segment))
                {
                    await this.WebSocket.SendAsync(segment, messageType, endOfMessage, CancellationToken.None).ConfigureAwait(false);
                    this.lastSend = DateTime.UtcNow;
                }
                else
                {
                    byte[] array = ArrayPool<byte>.Shared.Rent(memory.Length);
                    try
                    {
                        memory.CopyTo(array);
                        await this.WebSocket.SendAsync(new ArraySegment<byte>(array, 0, memory.Length), messageType, endOfMessage, CancellationToken.None).ConfigureAwait(false);
                        this.lastSend = DateTime.UtcNow;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(array);
                    }
                }
#endif

                bytesCopied += memory.Length;
            }
        }

        /// <inheritdoc />
        protected override ValueTask FlushAsync(CancellationToken cancellationToken) => default;
    }
}
