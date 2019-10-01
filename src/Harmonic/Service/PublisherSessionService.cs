using System;
using System.Linq;
using System.Collections.Generic;
using Harmonic.Controllers.Living;
using Harmonic.Networking.Rtmp.Messages;

namespace Harmonic.Service
{
    public class PublisherSessionService
    {
        private Dictionary<string, LivingStream> _pathMapToSession = new Dictionary<string, LivingStream>();
        private Dictionary<LivingStream, string> _sessionMapToPath = new Dictionary<LivingStream, string>();

        internal void RegisterPublisher(string publishingName, LivingStream session)
        {
            if (_pathMapToSession.ContainsKey(publishingName))
            {
                throw new InvalidOperationException("request instance is publishing");
            }
            if (_sessionMapToPath.ContainsKey(session))
            {
                throw new InvalidOperationException("request session is publishing");
            }
            _pathMapToSession.Add(publishingName, session);
            _sessionMapToPath.Add(session, publishingName);
        }

        internal void RemovePublisher(LivingStream session)
        {
            if (_sessionMapToPath.TryGetValue(session, out var publishingName))
            {
                _sessionMapToPath.Remove(session);
                _pathMapToSession.Remove(publishingName);
            }
        }
        public LivingStream FindPublisher(string publishingName)
        {
            if (_pathMapToSession.TryGetValue(publishingName, out var session))
            {
                return session;
            }
            return null;
        }

    }
}