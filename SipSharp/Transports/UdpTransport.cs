﻿using System;
using System.Net;
using System.Net.Sockets;
using SipSharp.Logging;
using SipSharp.Messages;
using SipSharp.Tools;
using SipSharp.Transports.Parser;

namespace SipSharp.Transports
{
    internal class UdpTransport : ITransport
    {
        private readonly ILogger _logger = LogFactory.CreateLogger(typeof (UdpTransport));
        private Socket _socket;
        private readonly MessageFactory _parsers;
        private EndPoint _serverEndPoint;

        /// <summary>
        /// Initializes a new instance of the <see cref="UdpTransport"/> class.
        /// </summary>
        /// <param name="bufferPool">The buffer pool.</param>
        /// <param name="parsers">The parsers.</param>
        public UdpTransport(MessageFactory parsers)
        {
            _parsers = parsers;
        }

        protected virtual Socket CreateSocket()
        {
            return new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }

        private void OnRead(IAsyncResult ar)
        {
            var buffer = (byte[]) ar.AsyncState;
            EndPoint endPoint = new IPEndPoint(IPAddress.Any, 3929);
            int bytesRead = 0;
            try
            {
                _logger.Trace("_socket.EndReceiveFrom");
                bytesRead = _socket.EndReceiveFrom(ar, ref endPoint);
            }
            catch(Exception err)
            {
                _logger.Warning("EndReceiveFrom failed: " + err.ToString());
            }


            // begin receiving another packet before starting to process this one
            byte[] newBuffer = BufferPool.Dequeue();

            try
            {
                _socket.BeginReceiveFrom(newBuffer, 0, newBuffer.Length, SocketFlags.None,
                                                ref _serverEndPoint, OnRead, newBuffer);
            }
            catch(Exception err)
            {
                _logger.Warning("BeginReceiveFrom failed, closing socket. Exception: " + err);
                BufferPool.Enqueue(newBuffer);
                BufferPool.Enqueue(buffer);
                _socket.Close();
                return;
            }

            if (bytesRead == 0)
                return;

            // Parse buffer.
            MessageFactoryContext factoryContext = _parsers.CreateNewContext(endPoint);
            try
            {
                int offset = factoryContext.Parse(buffer, 0, bytesRead);
                if (offset != bytesRead)
                    _logger.Error("Failed to parse complete message");
            }
            finally
            {
                BufferPool.Enqueue(buffer);
                _parsers.Release(factoryContext);
            }
        }

        

        private void OnSendComplete(IAsyncResult ar)
        {
            var context = (SendContext) ar.AsyncState;
            int bytesSent = _socket.EndSendTo(ar);
            if (context.buffer.Length != bytesSent)
            {
                _logger.Warning("Failed to send whole UDP message, " + bytesSent +
                                                         " of " + context.buffer.Length + " bytes to " + context.endPoint);
            }
            BufferPool.Enqueue(context.buffer);
            _logger.Trace("OnSendComplete");
        }


        #region Nested type: ClientContext

        private struct SendContext
        {
            public readonly EndPoint endPoint;
            public readonly byte[] buffer;

            public SendContext(EndPoint endPoint, byte[] buffer)
            {
                this.endPoint = endPoint;
                this.buffer = buffer;
            }
        }

        #endregion

        /// <summary>
        /// Start transport.
        /// </summary>
        /// <param name="listenerEndPoint">Address/port that clients should connect to.</param>
        /// <exception cref="ArgumentException"><see cref="EndPoint"/> is not of the type expected by the transport implementation</exception>
        /// <exception cref="ArgumentNullException"><c>endPoint</c> is null.</exception>
        public void Start(EndPoint listenerEndPoint)
        {
            if (listenerEndPoint == null)
                throw new ArgumentNullException("listenerEndPoint");
            IPEndPoint ep = listenerEndPoint as IPEndPoint;
            if (ep == null)
                throw new ArgumentException("Endpoint is not of type IPEndPoint", "listenerEndPoint");

            if (BufferPool == null)
                BufferPool = new ObjectPool<byte[]>(() => new byte[65535]);

            byte[] buffer = BufferPool.Dequeue();
            _socket = CreateSocket();
            _socket.Bind(ep);

            _logger.Trace("BeginReceiveFrom");
            _serverEndPoint = listenerEndPoint;
            _socket.BeginReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref _serverEndPoint, OnRead, buffer);
        }

        public void Send(EndPoint endPoint, byte[] buffer, int offset, int count)
        {
            _logger.Trace("Sending to " + endPoint);
            SendContext context = new SendContext(endPoint, buffer);
            _socket.BeginSendTo(buffer, 0, count, SocketFlags.None, endPoint, OnSendComplete,
                                       context);
            _logger.Trace("BeginSendTo");
        }

        /// <summary>
        /// Gets protocol used by this transporter.
        /// </summary>
        public string Protocol
        {
            get { return "UDP"; }
        }

        /// <summary>
        /// Gets port that the point is listening on.
        /// </summary>
        public int Port
        {
            get; set;
        }

        /// <summary>
        /// Gets of protocol is message based.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Message based protocols like UDP should only receive one (and a complete) message
        /// in each receive. While packet based protocols like TCP can receive partial, complete or multiple
        /// messages in one packet.
        /// </para>
        /// <para>This property should be used to </para>
        /// </remarks>
        //string IsMessageBasedProtocl{ get;}
        public ObjectPool<byte[]> BufferPool
        {
            set; private get;
        }

        public event UnhandledExceptionEventHandler UnhandledException = delegate{};
    }
}