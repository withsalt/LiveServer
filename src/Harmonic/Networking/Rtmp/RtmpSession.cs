﻿using Autofac;
using Harmonic.Controllers;
using Harmonic.Networking.Rtmp.Data;
using Harmonic.Networking.Rtmp.Messages;
using Harmonic.Networking.Rtmp.Messages.Commands;
using Harmonic.Networking;
using Harmonic.Rpc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace Harmonic.Networking.Rtmp
{
    public class RtmpSession : IDisposable
    {
        internal IOPipeLine IOPipeline { get; set; } = null;
        private Dictionary<uint, RtmpMessageStream> _messageStreams = new Dictionary<uint, RtmpMessageStream>();
        private Random _random = new Random();
        internal RtmpControlChunkStream ControlChunkStream { get; }
        public RtmpControlMessageStream ControlMessageStream { get; }
        public NetConnection NetConnection { get; }
        private RpcService _rpcService = null;
        public ConnectionInformation ConnectionInformation { get; internal set; }
        private object _allocCsidLocker = new object();
        private SortedList<uint, uint> _allocatedCsid = new SortedList<uint, uint>();

        internal RtmpSession(IOPipeLine ioPipeline)
        {
            IOPipeline = ioPipeline;
            ControlChunkStream = new RtmpControlChunkStream(this);
            ControlMessageStream = new RtmpControlMessageStream(this);
            _messageStreams.Add(ControlMessageStream.MessageStreamId, ControlMessageStream);
            NetConnection = new NetConnection(this);
            ControlMessageStream.RegisterMessageHandler<SetChunkSizeMessage>(HandleSetChunkSize);
            ControlMessageStream.RegisterMessageHandler<WindowAcknowledgementSizeMessage>(HandleWindowAcknowledgementSize);
            ControlMessageStream.RegisterMessageHandler<SetPeerBandwidthMessage>(HandleSetPeerBandwidth);
            ControlMessageStream.RegisterMessageHandler<AcknowledgementMessage>(HandleAcknowledgement);
            _rpcService = ioPipeline.Options.ServerLifetime.Resolve<RpcService>();
        }

        private void HandleAcknowledgement(AcknowledgementMessage ack)
        {
            Interlocked.Add(ref IOPipeline.ChunkStreamContext.WriteUnAcknowledgedSize, ack.BytesReceived * -1);
        }

        internal void AssertStreamId(uint msid)
        {
            Debug.Assert(_messageStreams.ContainsKey(msid));
        }
        internal uint MakeUniqueMessageStreamId()
        {
            // TBD use uint.MaxValue
            return (uint)_random.Next(1, 20);
        }

        internal uint MakeUniqueChunkStreamId()
        {
            // TBD make csid unique
            lock (_allocCsidLocker)
            {
                var next = _allocatedCsid.Any() ? _allocatedCsid.Last().Key : 2;
                if (uint.MaxValue == next)
                {
                    for (uint i = 0; i < uint.MaxValue; i++)
                    {
                        if (!_allocatedCsid.ContainsKey(i))
                        {
                            _allocatedCsid.Add(i, i);
                            return i;
                        }
                    }
                    throw new InvalidOperationException("too many chunk stream");
                }
                next += 1;
                _allocatedCsid.Add(next, next);
                return next;
            }
            
        }

        public T CreateNetStream<T>() where T: NetStream
        {
            var ret = IOPipeline.Options.ServerLifetime.Resolve<T>();
            ret.MessageStream = CreateMessageStream();
            ret.RtmpSession = this;
            ret.ChunkStream = CreateChunkStream();
            ret.MessageStream.RegisterMessageHandler<CommandMessage>(c => CommandHandler(ret, c));
            NetConnection.AddMessageStream(ret.MessageStream.MessageStreamId, ret);
            return ret;
        }

        public void DeleteNetStream(uint id)
        {
            if (NetConnection.NetStreams.TryGetValue(id, out var stream))
            {
                if (stream is IDisposable disp)
                {
                    disp.Dispose();
                }
                NetConnection.RemoveMessageStream(id);
            }
        }

        public T CreateCommandMessage<T>() where T: CommandMessage
        {
            var ret = Activator.CreateInstance(typeof(T), ConnectionInformation.AmfEncodingVersion);
            return ret as T;
        }

        public T CreateData<T>() where T : DataMessage
        {
            var ret = Activator.CreateInstance(typeof(T), ConnectionInformation.AmfEncodingVersion);
            return ret as T;
        }

        internal void CommandHandler(RtmpController controller, CommandMessage command)
        {
            MethodInfo method = null;
            object[] arguments = null;
            try
            {
                _rpcService.PrepareMethod(controller, command, out method, out arguments);
                var result = method.Invoke(controller, arguments);
                if (result != null)
                {
                    var resType = method.ReturnType;
                    if (resType.IsGenericType && resType.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        var tsk = result as Task;
                        tsk.ContinueWith(t =>
                        {
                            var taskResult = resType.GetProperty("Result").GetValue(result);
                            var retCommand = new ReturnResultCommandMessage(command.AmfEncodingVersion);
                            retCommand.IsSuccess = true;
                            retCommand.TranscationID = command.TranscationID;
                            retCommand.CommandObject = null;
                            retCommand.ReturnValue = taskResult;
                            _ = controller.MessageStream.SendMessageAsync(controller.ChunkStream, retCommand);
                        }, TaskContinuationOptions.OnlyOnRanToCompletion);
                        tsk.ContinueWith(t =>
                        {
                            var exception = tsk.Exception;
                            var retCommand = new ReturnResultCommandMessage(command.AmfEncodingVersion);
                            retCommand.IsSuccess = false;
                            retCommand.TranscationID = command.TranscationID;
                            retCommand.CommandObject = null;
                            retCommand.ReturnValue = exception.Message;
                            _ = controller.MessageStream.SendMessageAsync(controller.ChunkStream, retCommand);
                        }, TaskContinuationOptions.OnlyOnFaulted);
                    }
                    else if (resType == typeof(Task))
                    {
                        var tsk = result as Task;
                        tsk.ContinueWith(t =>
                        {
                            var exception = tsk.Exception;
                            var retCommand = new ReturnResultCommandMessage(command.AmfEncodingVersion);
                            retCommand.IsSuccess = false;
                            retCommand.TranscationID = command.TranscationID;
                            retCommand.CommandObject = null;
                            retCommand.ReturnValue = exception.Message;
                            _ = controller.MessageStream.SendMessageAsync(controller.ChunkStream, retCommand);
                        }, TaskContinuationOptions.OnlyOnFaulted);
                    }
                    else if (resType != typeof(void))
                    {
                        var retCommand = new ReturnResultCommandMessage(command.AmfEncodingVersion);
                        retCommand.IsSuccess = true;
                        retCommand.TranscationID = command.TranscationID;
                        retCommand.CommandObject = null;
                        retCommand.ReturnValue = result;
                        _ = controller.MessageStream.SendMessageAsync(controller.ChunkStream, retCommand);
                    }
                }
            }
            catch (Exception e)
            {
                var retCommand = new ReturnResultCommandMessage(command.AmfEncodingVersion);
                retCommand.IsSuccess = false;
                retCommand.TranscationID = command.TranscationID;
                retCommand.CommandObject = null;
                retCommand.ReturnValue = e.Message;
                _ = controller.MessageStream.SendMessageAsync(controller.ChunkStream, retCommand);
                return;
            }
        }

        internal bool FindController(string appName, out Type controllerType)
        {
            return IOPipeline.Options.RegisteredControllers.TryGetValue(appName.ToLower(), out controllerType);
        }

        public void Close()
        {
            IOPipeline.Disconnect();
        }

        private RtmpMessageStream CreateMessageStream()
        {
            var stream = new RtmpMessageStream(this);
            MessageStreamCreated(stream);
            return stream;
        }

        public RtmpChunkStream CreateChunkStream()
        {
            return new RtmpChunkStream(this);
        }

        internal void ChunkStreamDestroyed(RtmpChunkStream rtmpChunkStream)
        {
            lock (_allocCsidLocker)
            {
                _allocatedCsid.Remove(rtmpChunkStream.ChunkStreamId);
            }
        }

        internal Task SendMessageAsync(uint chunkStreamId, Message message)
        {
            return IOPipeline.ChunkStreamContext.MultiplexMessageAsync(chunkStreamId, message);
        }

        internal void MessageStreamCreated(RtmpMessageStream messageStream)
        {
            _messageStreams[messageStream.MessageStreamId] = messageStream;
        }

        internal void MessageStreamDestroying(RtmpMessageStream messageStream)
        {
            _messageStreams.Remove(messageStream.MessageStreamId);
        }

        internal void MessageArrived(Message message)
        {
            if (_messageStreams.TryGetValue(message.MessageHeader.MessageStreamId.Value, out var stream))
            {
                stream.MessageArrived(message);
            }
            else
            {
                Console.WriteLine($"Warning: aborted message stream id: {message.MessageHeader.MessageStreamId}");
            }
        }

        internal void Acknowledgement(uint bytesReceived)
        {
            _ = ControlMessageStream.SendMessageAsync(ControlChunkStream, new AcknowledgementMessage()
            {
                BytesReceived = bytesReceived
            });
        }

        private void HandleSetPeerBandwidth(SetPeerBandwidthMessage message)
        {
            if (IOPipeline.ChunkStreamContext.WriteWindowAcknowledgementSize.HasValue && message.LimitType == LimitType.Soft && message.WindowSize > IOPipeline.ChunkStreamContext.WriteWindowAcknowledgementSize)
            {
                return;
            }
            if (IOPipeline.ChunkStreamContext.PreviousLimitType.HasValue && message.LimitType == LimitType.Dynamic && IOPipeline.ChunkStreamContext.PreviousLimitType != LimitType.Hard)
            {
                return;
            }
            IOPipeline.ChunkStreamContext.PreviousLimitType = message.LimitType;
            IOPipeline.ChunkStreamContext.WriteWindowAcknowledgementSize = message.WindowSize;
            SendControlMessageAsync(new WindowAcknowledgementSizeMessage()
            {
                WindowSize = message.WindowSize
            });
        }

        private void HandleWindowAcknowledgementSize(WindowAcknowledgementSizeMessage message)
        {
            IOPipeline.ChunkStreamContext.ReadWindowAcknowledgementSize = message.WindowSize;
        }

        private void HandleSetChunkSize(SetChunkSizeMessage setChunkSize)
        {
            IOPipeline.ChunkStreamContext.ReadChunkSize = (int)setChunkSize.ChunkSize;
        }

        public Task SendControlMessageAsync(Message message)
        {
            if (message.MessageHeader.MessageType == MessageType.WindowAcknowledgementSize)
            {
                IOPipeline.ChunkStreamContext.WriteWindowAcknowledgementSize = ((WindowAcknowledgementSizeMessage)message).WindowSize;
            }
            return ControlMessageStream.SendMessageAsync(ControlChunkStream, message);
        }

        #region IDisposable Support
        private bool disposedValue = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    NetConnection.Dispose();
                    ControlChunkStream.Dispose();
                    ControlMessageStream.Dispose();
                }

                disposedValue = true;
            }
        }

        // ~RtmpSession() {
        //   Dispose(false);
        // }

        public void Dispose()
        {
            Dispose(true);
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}
