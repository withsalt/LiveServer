﻿using Autofac;
using Harmonic.Controllers;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Harmonic.Hosting
{
    public class WebSocketOptions
    {
        public IPEndPoint BindEndPoint { get; set; }
        public Regex UrlMapping { get; set; } = new Regex(@"/(?<controller>[a-zA-Z0-9]+)/(?<streamName>[a-zA-Z0-9\.]+)", RegexOptions.IgnoreCase);

        internal Dictionary<string, Type> _controllers = new Dictionary<string, Type>();

        internal RtmpServerOptions _serverOptions = null;

        internal void RegisterController<T>() where T: WebSocketController
        {
            RegisterController(typeof(T));
        }

        internal void RegisterController(Type controllerType)
        {
            if (!typeof(WebSocketController).IsAssignableFrom(controllerType))
            {
                throw new ArgumentException("controller not inherit from WebSocketController");
            }
            _controllers.Add(controllerType.Name.Replace("Controller", "").ToLower(), controllerType);
            _serverOptions._builder.RegisterType(controllerType).AsSelf();
        }

    }
}
