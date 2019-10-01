﻿using System;
using System.Collections.Generic;
using System.Text;

namespace Harmonic.Networking.Flv.Data
{
    public class FlvAudioData
    {
        public SoundFormat SoundFormat { get; set; }
        public SoundRate SoundRate { get; set; }
        public SoundSize SoundSize { get; set; }
        public SoundType SoundType { get; set; }
        public AudioData AudioData { get; set; }
    }
}
