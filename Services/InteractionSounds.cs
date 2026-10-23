using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using NAudio.Wave;

namespace VNotch.Services;

internal enum InteractionCue { Click, ToggleOn, ToggleOff, Navigate, Select, Slider, Menu, Confirm, Delete, Transport }

// One shared, asynchronous playback channel. No files, polling or audio capture.
internal sealed class InteractionSoundPlayer : IDisposable
{
    internal const int VariantCount = 6;
    private readonly Dictionary<(InteractionCue Cue, int Variant), byte[]> _sounds = new();
    private WaveOutEvent? _output;
    private RawSourceWaveStream? _source;
    private VolumeWaveProvider16? _gain;
    private float _volume = .55f;
    private readonly Action<InteractionCue, int>? _playOverride;
    private readonly int[] _lastVariants = new int[Enum.GetValues<InteractionCue>().Length];
    private readonly long[] _lastCuePlayed = new long[Enum.GetValues<InteractionCue>().Length];
    private readonly Func<long> _clock;
    private long? _lastPlayed;
    private int _rapidBurstCount;
    private bool _reportedPlaybackFailure;
    internal bool Enabled { get; set; }
    internal float Volume
    {
        get => _volume;
        set
        {
            _volume = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
            if (_gain != null) _gain.Volume = _volume;
            if (_volume == 0) DisposeOutput();
        }
    }

    internal InteractionSoundPlayer(Action<InteractionCue, int>? play = null, Func<long>? clock = null)
    {
        _playOverride = play;
        _clock = clock ?? (() => Environment.TickCount64);
        Array.Fill(_lastVariants, -1);
        Array.Fill(_lastCuePlayed, long.MinValue / 2);
    }

    internal void Play(InteractionCue cue)
    {
        if (!Enabled || Volume == 0) return;
        long now = _clock();
        if (_lastPlayed is long last)
        {
            long elapsed = now - last;
            // Adaptive anti-spam: base 65 ms global gate, plus per-cue cooldown and burst backoff when real UI events spam.
            if (elapsed < 65) return;
            long cueElapsed = now - _lastCuePlayed[(int)cue];
            int sameCueCooldown = _playOverride != null ? 65 : cue switch
            {
                InteractionCue.Slider => 130,
                InteractionCue.Confirm or InteractionCue.Delete => 180,
                InteractionCue.ToggleOn or InteractionCue.ToggleOff or InteractionCue.Navigate => 140,
                _ => 110
            };
            if (cueElapsed < sameCueCooldown) return;
            _rapidBurstCount = elapsed < 220 ? Math.Min(6, _rapidBurstCount + 1) : 0;
            if (_playOverride == null && _rapidBurstCount >= 3 && elapsed < 65 + _rapidBurstCount * 35) return;
        }
        _lastPlayed = now;
        _lastCuePlayed[(int)cue] = now;
        int previous = _lastVariants[(int)cue];
        int variant = Random.Shared.Next(previous < 0 ? VariantCount : VariantCount - 1);
        if (previous >= 0 && variant >= previous) variant++;
        _lastVariants[(int)cue] = variant;
        try
        {
            if (_playOverride != null) { _playOverride(cue, variant); return; }
            if (!_sounds.TryGetValue((cue, variant), out var sound))
            {
                sound = CreateWave(cue, variant);
                _sounds.Add((cue, variant), sound);
            }
            DisposeOutput();
            _source = new RawSourceWaveStream(new MemoryStream(sound, 44, sound.Length - 44, writable: false), new WaveFormat(44100, 16, 1));
            // Software gain affects only these PCM samples, never media/system volume.
            // Duck gain slightly during rapid consecutive clicks so fast clicking never fatigues the ear.
            float burstDuck = _rapidBurstCount > 0 ? Math.Max(.45f, 1f - _rapidBurstCount * .12f) : 1f;
            _gain = new VolumeWaveProvider16(_source) { Volume = Volume * burstDuck };
            _output = new WaveOutEvent { DesiredLatency = 50, NumberOfBuffers = 2 };
            _output.Init(_gain);
            _output.Play();
            _reportedPlaybackFailure = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or TimeoutException or NAudio.MmException)
        {
            DisposeOutput();
            if (!_reportedPlaybackFailure)
            {
                _reportedPlaybackFailure = true;
                RuntimeLog.Error("SFX", ex, "Interaction sound playback failed");
            }
            // Feedback must never interrupt the user's action if audio is unavailable.
        }
    }

    // Authentic acoustic switch strokes extracted from Assets/SPL_CLICK_SFX.m4a (press & release kernels).
    private static ReadOnlySpan<short> SplPressKernel => new short[] { 0, -2, 0, 18, -4, 52, 59, 76, 172, 159, 179, 146, 533, 576, 842, 1629, 2212, 1239, -982, -4264, -6302, 1777, 7998, 7974, 6831, 3615, -2580, -5987, -6748, -6628, -4862, -3882, -5254, -3140, -3718, -6893, -4432, -3401, -962, 4025, -1696, -4852, -2883, -2446, 3456, 5786, 5554, 6543, 5057, 3379, 2524, 2854, 4576, 2966, -749, -2700, -1347, 1285, 1929, -34, -2167, -2053, -1595, -124, 748, 739, 1445, 1956, 2202, 1545, 1037, 442, -978, -1676, -847, -751, -1009, 235, 625, -230, -368, -527, -1092, 68, 543, -993, -1004, -367, -84, -197, -1230, -1222, 215, 1087, 467, -633, -864, -295, 315, 124, -1683, -2266, -226, 909, 352, -501, -314, 640, 973, 406, 60, 350, 809, 1382, 969, -83, 119, 1121, 996, 81, -758, -1133, -700, 352, 555, -1087, -1116, -940, -1188, -290, -168, 297, 651, 97, 500, 1091, 1406, 660, 207, 731, 394, 198, 83, -213, 428, 173, -184, 701, 294, 138, 393, 204, 159, -140, -302, -500, -545, -163, -545, -447, -22, -549, -1219, -1330, -873, -994, -1012, -506, -359, -127, -496, 233, 794, 24, 346, 614, 898, 1001, 378, 478, 615, 813, 322, -76, 353, 190, -108, -570, -554, -22, -167, -408, -518, -282, 18, -51, 34, 96, -30, 250, 534, 228, 142, -164, -215, 355, -115, -312, -218, -89, 334, 37, -43, -100, -290, 324, 154, -459, -705, -492, -26, 111, -35, -272, -261, -359, -356, 54, -80, -123, -93, -138, 16, -33, 215, 468, 230, -48, -381, -136, 135, -15, 76, 341, 441, 102, -186, -160, 13, 208, -135, -172, -123, -65, 257, 123, 175, 373, 358, 264, -181, -79, 202, 280, 285, 56, -112, -37, 64, -73, -157, -223, -159, 83, -66, -261, -252, -45, 36, -94, -35, -79, -116, 23, 124, 133, -33, -31, 44, 20, -12, -91, 15, 58, 37, 291, 240, 6, -28, 159, 266, 161, 153, 82, -15, -52, -92, 79, 65, -48, -60, -126, -111, -59, 17, 74, -60, -107, -82, -17, 77, 80, 169, 153, -1, -80, -101, 30, 86, 33, 0, -88, -55, 2, -34, -16, 41, 77, 54, -54, -58, 45, 98, 50, 11, 40, 33, 12, 0, 23, 56, -4, -32, -38, -43, -22, -16, -18, -22, -15, 17, 28, -8, -16, 8, 7, 1, 2, -5, -16, -21, -10, 16, 26, 8, -8, -8, -1, 3, 2, 0, -1, 0, 0 };
    private static ReadOnlySpan<short> SplReleaseKernel => new short[] { 0, 1, 4, 1, -2, -6, -22, -48, -110, -141, -162, -292, -381, -684, -1331, -1784, -1868, -973, 2437, 5417, 3498, -5794, -8000, -7948, -6434, -3222, -1940, 1132, 6121, 7543, 7352, 5861, 3538, 5296, 7164, 6723, 4532, 2095, -492, 591, 4216, 6401, 7082, 5817, 311, -4577, -5236, -4300, -3916, -4710, -5981, -6856, -7474, -7482, -6294, -4058, -2735, -2064, -1570, -1079, 845, 2761, 3128, 2827, 1475, -23, -598, -566, 272, 1024, 1029, 1056, 1350, 1310, 1639, 2032, 1453, 500, 75, 119, 308, 809, 678, 93, 148, 640, 953, 940, 1082, 1609, 2048, 1224, -127, -606, -379, 241, 515, 256, -90, 181, 686, 863, 693, -270, -431, -366, -991, -1472, -1733, -1490, -1389, -1749, -2075, -2123, -1465, -1080, -1264, -1560, -1497, -785, 37, 468, 465, 193, 238, 536, 775, 1523, 1898, 1418, 669, 188, 456, 673, 413, -46, -243, 267, 624, 678, 627, 708, 954, 913, 618, 380, 429, 422, 385, 319, 55, 130, 502, 467, -133, -564, -612, -403, -439, -701, -741, -1199, -1461, -1161, -806, -653, -858, -872, -456, -315, -549, -765, -558, -11, 341, 278, 99, 290, 454, 394, 506, 705, 949, 989, 863, 878, 917, 903, 689, 474, 463, 442, 480, 401, 188, -94, -183, -61, -200, -523, -693, -434, -233, -439, -574, -510, -241, -82, -268, -553, -647, -561, -401, -301, -323, -389, -408, -64, 311, 460, 370, -8, -114, 213, 635, 834, 624, 342, 206, 180, 144, 164, 224, 82, -224, -433, -358, -156, 20, 103, -35, -274, -501, -545, -326, -99, 23, 35, -31, -42, 53, 215, 343, 295, 84, -54, -104, -159, -192, -175, -25, 41, -126, -349, -296, -33, 93, 135, 105, 161, 203, 14, 52, 158, 215, 232, 85, 18, 42, 106, 157, 200, 241, 81, -34, -36, -24, -38, -91, -126, -146, -95, -68, -127, -194, -238, -122, -34, -60, -53, -90, -166, -185, -139, -21, 105, 153, 111, 58, 93, 162, 204, 188, 117, 82, 73, 83, 141, 131, 99, 49, -85, -163, -78, 100, 162, 44, -75, -113, -99, -75, -43, 33, 70, 8, -86, -152, -106, 17, 50, -5, -53, -70, -60, -46, -21, 18, 49, 37, -4, -15, -2, 33, 58, 30, 9, 21, 33, 39, 20, 0, 10, 14, 5, 0, -5, -1, 5, 5, 4, -3, -14, -24, -24, -7, 9, 8, -1, -5, -2, 1, 1, 0, 0 };

    private static double SampleKernel(ReadOnlySpan<short> kernel, double pos)
    {
        if (pos <= 0 || pos >= kernel.Length - 1) return 0;
        int idx = (int)pos;
        double frac = pos - idx;
        double y0 = kernel[Math.Max(0, idx - 1)];
        double y1 = kernel[idx];
        double y2 = kernel[idx + 1];
        double y3 = kernel[Math.Min(kernel.Length - 1, idx + 2)];
        double c0 = y1;
        double c1 = .5 * (y2 - y0);
        double c2 = y0 - 2.5 * y1 + 2 * y2 - .5 * y3;
        double c3 = .5 * (y3 - y0) + 1.5 * (y1 - y2);
        return ((c3 * frac + c2) * frac + c1) * frac + c0;
    }

    internal static byte[] CreateWave(InteractionCue cue, int variant = 0)
    {
        if (variant < 0 || variant >= VariantCount) throw new ArgumentOutOfRangeException(nameof(variant));
        const int rate = 44100;
        double vPitch = 1 + (variant - 2.5) * .018;
        double vTime = 1 + (variant - 2.5) * .010;
        var k1 = SplPressKernel;
        var k2 = SplReleaseKernel;

        (double dur, double cut) = cue switch
        {
            InteractionCue.Click => (.022, .085),
            InteractionCue.Select => (.021, .090),
            InteractionCue.Slider => (.017, .092),
            InteractionCue.Menu => (.026, .082),
            InteractionCue.Transport => (.026, .080),
            InteractionCue.ToggleOn => (.032, .088),
            InteractionCue.ToggleOff => (.031, .080),
            InteractionCue.Navigate => (.029, .085),
            InteractionCue.Confirm => (.041, .088),
            InteractionCue.Delete => (.030, .076),
            _ => (.022, .085)
        };
        int samples = (int)(rate * dur);
        var floatBuf = new double[samples];

        switch (cue)
        {
            case InteractionCue.Click:
                {
                    // 1. CLICK: Deep, muted PBT keycap thock (165 Hz sub + slow-pitched k2 body, zero k1 snap)
                    int start = (int)(rate * .0006);
                    int end = (int)(rate * .0136);
                    for (int i = start; i < end; i++)
                    {
                        double dt = (double)(i - start) / rate;
                        double att = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt / .0022)));
                        double pos = (i - start) * .48 * vPitch;
                        double fade = i > end - 110 ? .5 * (1 - Math.Cos(Math.PI * (end - 1 - i) / 110.0)) : 1.0;
                        double warmSub = 4600 * Math.Sin(2 * Math.PI * 165 * vPitch * dt) * att * Math.Exp(-dt * 230);
                        floatBuf[i] = fade * (.55 * att * SampleKernel(k2, pos) + warmSub);
                    }
                    break;
                }
            case InteractionCue.Select:
                {
                    // 2. SELECT: Soft low wooden tap (215 Hz + 322 Hz muted fifth, ultra-warm)
                    int start = (int)(rate * .0006);
                    for (int i = start; i < samples - 1; i++)
                    {
                        double dt = (double)(i - start) / rate;
                        double att = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt / .0024)));
                        double env = att * Math.Exp(-dt * 260);
                        double wood = 5000 * (.84 * Math.Sin(2 * Math.PI * 215 * vPitch * dt) + .16 * Math.Sin(2 * Math.PI * 322 * vPitch * dt)) * env;
                        double tap = .28 * att * SampleKernel(k2, (i - start) * .54 * vPitch);
                        floatBuf[i] = wood + tap;
                    }
                    break;
                }
            case InteractionCue.Slider:
                {
                    // 3. SLIDER: Soft velvet rotary detent (195 Hz warm pulse, no sideband ring)
                    int start = (int)(rate * .0005);
                    for (int i = start; i < samples - 1; i++)
                    {
                        double dt = (double)(i - start) / rate;
                        double att = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt / .0020)));
                        double env = att * Math.Exp(-dt * 320);
                        double detent = 5200 * Math.Sin(2 * Math.PI * 195 * vPitch * dt) * env;
                        double tick = .25 * att * SampleKernel(k2, (i - start) * .58 * vPitch);
                        floatBuf[i] = detent + tick;
                    }
                    break;
                }
            case InteractionCue.Menu:
                {
                    // 4. MENU: Deep air-cushion bloom (upward glide 135 Hz -> 225 Hz)
                    int start = (int)(rate * .0006);
                    for (int i = start; i < samples - 1; i++)
                    {
                        double dt = (double)(i - start) / rate;
                        double att = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt / .0026)));
                        double env = att * Math.Exp(-dt * 195);
                        double phase = 2 * Math.PI * vPitch * (225 * dt + (90.0 / 150.0) * (Math.Exp(-dt * 150) - 1));
                        double pop = 5800 * Math.Sin(phase) * env;
                        double lip = .24 * att * SampleKernel(k2, (i - start) * .50 * vPitch);
                        floatBuf[i] = pop + lip;
                    }
                    break;
                }
            case InteractionCue.Transport:
                {
                    // 5. TRANSPORT: Plush heavy damped button (145 Hz sub + 5.8 ms soft low-thock lock)
                    int start = (int)(rate * .0006);
                    int lockStart = (int)(rate * (.0058 * vTime));
                    for (int i = start; i < samples - 1; i++)
                    {
                        double dt = (double)(i - start) / rate;
                        double att = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt / .0024)));
                        double subEnv = att * Math.Exp(-dt * 200);
                        double sub = 5600 * Math.Sin(2 * Math.PI * 145 * vPitch * dt) * subEnv;
                        double chug = .42 * att * SampleKernel(k2, (i - start) * .48 * vPitch);
                        double lockClick = i >= lockStart ? .38 * SampleKernel(k2, (i - lockStart) * .56 * vPitch) : 0;
                        floatBuf[i] = sub + chug + lockClick;
                    }
                    break;
                }
            case InteractionCue.ToggleOn:
                {
                    // 6. TOGGLE ON: Warm 2-step ascending low marimba fourth (165 Hz E3 -> 220 Hz A3)
                    int s1 = (int)(rate * .0006);
                    int s2 = (int)(rate * (.0108 * vTime));
                    for (int i = s1; i < samples - 1; i++)
                    {
                        double dt1 = (double)(i - s1) / rate;
                        double att1 = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt1 / .0024)));
                        double env1 = att1 * Math.Exp(-dt1 * 240);
                        floatBuf[i] += .28 * att1 * SampleKernel(k2, (i - s1) * .50 * vPitch) + 4800 * Math.Sin(2 * Math.PI * 164.81 * vPitch * dt1) * env1;
                        if (i >= s2)
                        {
                            double dt2 = (double)(i - s2) / rate;
                            double att2 = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt2 / .0024)));
                            double env2 = att2 * Math.Exp(-dt2 * 210);
                            floatBuf[i] += .30 * att2 * SampleKernel(k2, (i - s2) * .58 * vPitch) + 5200 * (.88 * Math.Sin(2 * Math.PI * 220.00 * vPitch * dt2) + .12 * Math.Sin(2 * Math.PI * 329.63 * vPitch * dt2)) * env2;
                        }
                    }
                    break;
                }
            case InteractionCue.ToggleOff:
                {
                    // 7. TOGGLE OFF: Warm 2-step descending settle (207.65 Hz G#3 -> 146.83 Hz D3)
                    int s1 = (int)(rate * .0006);
                    int s2 = (int)(rate * (.0105 * vTime));
                    for (int i = s1; i < samples - 1; i++)
                    {
                        double dt1 = (double)(i - s1) / rate;
                        double att1 = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt1 / .0024)));
                        double env1 = att1 * Math.Exp(-dt1 * 250);
                        floatBuf[i] += .28 * att1 * SampleKernel(k2, (i - s1) * .56 * vPitch) + 4800 * Math.Sin(2 * Math.PI * 207.65 * vPitch * dt1) * env1;
                        if (i >= s2)
                        {
                            double dt2 = (double)(i - s2) / rate;
                            double att2 = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt2 / .0024)));
                            double env2 = att2 * Math.Exp(-dt2 * 210);
                            floatBuf[i] += .34 * att2 * SampleKernel(k2, (i - s2) * .46 * vPitch) + 5200 * Math.Sin(2 * Math.PI * 146.83 * vPitch * dt2) * env2;
                        }
                    }
                    break;
                }
            case InteractionCue.Navigate:
                {
                    // 8. NAVIGATE: Soft low-mid warm breath (155 Hz -> 245 Hz) + twin muted cushion taps
                    int s1 = (int)(rate * .0006);
                    int s2 = (int)(rate * (.0078 * vTime));
                    for (int i = s1; i < samples - 1; i++)
                    {
                        double dt = (double)(i - s1) / rate;
                        double fGlide = (155 + 90 * Math.Min(1.0, dt / .022)) * vPitch;
                        double swooshEnv = Math.Sin(Math.PI * Math.Min(1.0, dt / .025)) * Math.Exp(-dt * 135);
                        double tap = .30 * SampleKernel(k2, (i - s1) * .50 * vPitch);
                        if (i >= s2) tap += .35 * SampleKernel(k2, (i - s2) * .58 * vPitch);
                        floatBuf[i] = tap + 4600 * Math.Sin(2 * Math.PI * fGlide * dt) * swooshEnv;
                    }
                    break;
                }
            case InteractionCue.Confirm:
                {
                    // 9. CONFIRM: Warm 3-note low-register wooden chord (164.81 Hz E3 -> 207.65 Hz G#3 -> 246.94 Hz B3)
                    ReadOnlySpan<(double Delay, double Freq, double Amp, double KMix, double KRate)> notes = stackalloc[]
                    {
                    (.0006, 164.81, 4400.0, .22, .48),
                    (.0092, 207.65, 4800.0, .22, .54),
                    (.0182, 246.94, 5400.0, .26, .60)
                };
                    foreach (var n in notes)
                    {
                        int st = (int)(rate * n.Delay * vTime);
                        for (int i = st; i < samples - 1; i++)
                        {
                            double dt = (double)(i - st) / rate;
                            double att = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt / .0024)));
                            double env = att * Math.Exp(-dt * 215);
                            double chime = n.Amp * (.90 * Math.Sin(2 * Math.PI * n.Freq * vPitch * dt) + .10 * Math.Sin(2 * Math.PI * n.Freq * 1.5 * vPitch * dt)) * env;
                            double sp = n.KMix * att * SampleKernel(k2, (i - st) * n.KRate * vPitch);
                            floatBuf[i] += chime + sp;
                        }
                    }
                    break;
                }
            case InteractionCue.Delete:
                {
                    // 10. DELETE: Deep muted cushion drop (195 Hz -> 118 Hz downward glide + soft k2 double-thud)
                    int s1 = (int)(rate * .0006);
                    int s2 = (int)(rate * (.0086 * vTime));
                    for (int i = s1; i < samples - 1; i++)
                    {
                        double dt = (double)(i - s1) / rate;
                        double att = .5 * (1 - Math.Cos(Math.PI * Math.Min(1.0, dt / .0024)));
                        double env = att * Math.Exp(-dt * 195);
                        double phase = 2 * Math.PI * vPitch * (118 * dt + (77.0 / 140.0) * (1 - Math.Exp(-dt * 140)));
                        double drop = 5400 * Math.Sin(phase) * env;
                        double thud = .30 * att * SampleKernel(k2, (i - s1) * .50 * vPitch);
                        if (i >= s2) thud += .38 * SampleKernel(k2, (i - s2) * .42 * vPitch);
                        floatBuf[i] = drop + thud;
                    }
                    break;
                }
        }

        using var stream = new MemoryStream(44 + samples * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);

        double lp1 = 0, lp2 = 0, lp3 = 0, lp4 = 0;
        double c = cut * (1 + (variant - 2.5) * .012);
        int clickEnd = cue == InteractionCue.Click ? (int)(rate * .014) : samples;
        for (int i = 0; i < samples; i++)
        {
            if (i == 0 || i == samples - 1 || i >= clickEnd)
            {
                writer.Write((short)0);
                continue;
            }
            double w = 1.0;
            if (i < 64) w = .5 * (1 - Math.Cos(Math.PI * i / 64.0));
            else if (i > clickEnd - 112) w = .5 * (1 - Math.Cos(Math.PI * (clickEnd - 1 - i) / 112.0));
            double v = floatBuf[i] * w;
            lp1 += c * (v - lp1);
            lp2 += c * (lp1 - lp2);
            lp3 += c * (lp2 - lp3);
            lp4 += c * (lp3 - lp4);
            writer.Write((short)Math.Round(lp4));
        }
        byte[] wave = stream.ToArray();
        NormalizeLoudness(wave);
        return wave;
    }

    // Keep the short click audible without large peaks or a loud sustained body.
    // User gain remains independent of this asset calibration.
    private static void NormalizeLoudness(byte[] wave)
    {
        double energy = 0;
        int peak = 0, count = (wave.Length - 44) / 2;
        for (int offset = 44; offset < wave.Length; offset += 2)
        {
            int sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(offset, 2));
            energy += (double)sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        if (peak == 0) return;
        const double targetRms = 1304.52; // -28 dBFS, before the user's 0–100% gain
        double gain = Math.Min(targetRms / Math.Sqrt(energy / count), 6553.0 / peak);
        for (int offset = 44; offset < wave.Length; offset += 2)
        {
            var sample = wave.AsSpan(offset, 2);
            short value = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(sample);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(sample, (short)Math.Round(value * gain));
        }
    }

    public void Dispose()
    {
        DisposeOutput();
        _sounds.Clear();
    }

    private void DisposeOutput()
    {
        try
        {
            _output?.Stop();
            _output?.Dispose();
        }
        catch (NAudio.MmException) { /* A device can disappear during playback. */ }
        finally { _output = null; _gain = null; _source?.Dispose(); _source = null; }
    }
}

internal static class InteractionSounds
{
    private static readonly InteractionSoundPlayer Player = new();
    private static bool _initialized;

    internal static void Initialize(bool enabled, double volume)
    {
        if (!_initialized)
        {
            _initialized = true;
            EventManager.RegisterClassHandler(typeof(Window), ButtonBase.ClickEvent, new RoutedEventHandler(OnClick), true);
            EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPointerDown), true);
            EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnPointerUp), true);
            EventManager.RegisterClassHandler(typeof(Window), Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted), true);
            EventManager.RegisterClassHandler(typeof(Window), Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnSelection), true);
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent, new RoutedEventHandler(OnMenuClick), true);
        }
        Configure(enabled, volume);
    }

    internal static void Configure(bool enabled, double volume)
    {
        // Tests and setup windows do not initialize application feedback.
        if (!_initialized) return;
        Player.Enabled = enabled;
        Player.Volume = (float)volume;
        if (!enabled) Player.Dispose();
    }

    internal static void Shutdown() { Player.Enabled = false; Player.Dispose(); }
    internal static void Preview() { if (_initialized) Player.Play(InteractionCue.Select); }
    // The notch intentionally accepts pointer input without activating its window.
    private static bool CanPlay(object sender) => sender is Window { IsVisible: true, IsEnabled: true };

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanPlay(sender) || e.OriginalSource is not ButtonBase { IsEnabled: true } button) return;
        var cue = ClassifyAction(button);
        Player.Play(cue == InteractionCue.Click && button is ToggleButton toggle
            ? toggle.IsChecked == true ? InteractionCue.ToggleOn : InteractionCue.ToggleOff
            : cue);
    }

    private static void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is MenuItem { IsEnabled: true, HasItems: false } menu)
        {
            var cue = ClassifyAction(menu);
            Player.Play(menu.IsCheckable ? menu.IsChecked ? InteractionCue.ToggleOn : InteractionCue.ToggleOff
                : cue == InteractionCue.Click ? InteractionCue.Menu : cue);
        }
    }

    private static void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanPlay(sender) || e.ChangedButton != MouseButton.Left) return;
        bool customAction = false;
        var cue = InteractionCue.Click;
        for (var node = e.OriginalSource as DependencyObject; node != null && node is not Window; node = Parent(node))
        {
            // Standard controls have semantic events; editable content stays quiet.
            if (node is ButtonBase or Slider or TextBoxBase or PasswordBox or Thumb) return;
            // ComboBoxItem and TabItem fire OnSelection when their selection actually changes; only ListBoxItem needs direct pointer sound.
            if (node is ListBoxItem) { Player.Play(InteractionCue.Select); return; }
            if (node is ComboBoxItem or TabItem or Selector) return;
            if (node is FrameworkElement { IsEnabled: false }) return;
            if (node is FrameworkElement element)
            {
                if (element.Cursor == Cursors.Hand) customAction = true;
                if (cue == InteractionCue.Click) cue = ClassifyAction(element);
            }
        }
        if (customAction) Player.Play(cue);
    }

    private static void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (CanPlay(sender) && !e.Canceled && e.OriginalSource is DependencyObject source)
            for (var node = source; node != null; node = Parent(node))
                if (node is Slider slider)
                {
                    if (slider.Name != "InteractionSoundVolumeSlider") Player.Play(InteractionCue.Slider);
                    break;
                }
    }

    private static void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanPlay(sender) || e.ChangedButton != MouseButton.Left) return;
        for (var node = e.OriginalSource as DependencyObject; node != null; node = Parent(node))
        {
            if (node is Thumb) return;
            if (node is Slider slider)
            {
                // The volume control previews its chosen gain in its own handler.
                if (slider.Name != "InteractionSoundVolumeSlider") Player.Play(InteractionCue.Slider);
                return;
            }
        }
    }

    private static void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (CanPlay(sender) && e.AddedItems.Count > 0 && e.OriginalSource is Selector { IsKeyboardFocusWithin: true } selector &&
            (selector is TabControl or ComboBox))
            Player.Play(selector is TabControl ? InteractionCue.Navigate : InteractionCue.Select);
    }

    internal static InteractionCue ClassifyAction(FrameworkElement element)
    {
        string name = element.Name;
        if (name.Contains("Cancel", StringComparison.Ordinal) || name.Contains("Close", StringComparison.Ordinal) || name.Contains("Dismiss", StringComparison.Ordinal)) return InteractionCue.Click;
        if (name.Contains("ClearSearch", StringComparison.Ordinal) || name.Contains("SearchingClear", StringComparison.Ordinal) || name.Contains("Pill", StringComparison.Ordinal) || name.Contains("Reveal", StringComparison.Ordinal)) return InteractionCue.Select;
        if (element is TabItem || name.StartsWith("Nav", StringComparison.Ordinal) || name.Contains("Tab", StringComparison.Ordinal) || name.Contains("IconButton", StringComparison.Ordinal) || name.Contains("BackButton", StringComparison.Ordinal) || name.Contains("Header", StringComparison.Ordinal)) return InteractionCue.Navigate;
        if (name.Contains("Delete", StringComparison.Ordinal) || name.Contains("Remove", StringComparison.Ordinal) || name.Contains("Clear", StringComparison.Ordinal) || name.Contains("Reset", StringComparison.Ordinal) || name.Contains("Empty", StringComparison.Ordinal) || name.Contains("Disconnect", StringComparison.Ordinal)) return InteractionCue.Delete;
        if (name.Contains("Save", StringComparison.Ordinal) || name.Contains("Apply", StringComparison.Ordinal) || name.Contains("Copy", StringComparison.Ordinal) || name.Contains("Confirm", StringComparison.Ordinal) || name.Contains("Approve", StringComparison.Ordinal) || name.Contains("Unlock", StringComparison.Ordinal) || name.Contains("Connect", StringComparison.Ordinal) || name.Contains("Export", StringComparison.Ordinal) || name.Contains("Import", StringComparison.Ordinal) || name.Contains("Download", StringComparison.Ordinal) || name.Contains("Replace", StringComparison.Ordinal) || name.Contains("TranslateButton", StringComparison.Ordinal)) return InteractionCue.Confirm;
        if (name.Contains("Play", StringComparison.Ordinal) || name.Contains("Pause", StringComparison.Ordinal) || name.Contains("Next", StringComparison.Ordinal) || name.Contains("Prev", StringComparison.Ordinal) || name.Contains("CountdownStart", StringComparison.Ordinal) || name.Contains("CountdownPlus", StringComparison.Ordinal) || name.Contains("CountdownMinus", StringComparison.Ordinal) || name.Contains("StopButton", StringComparison.Ordinal) || name.Contains("MuteButton", StringComparison.Ordinal)) return InteractionCue.Transport;
        if (name.StartsWith("Menu", StringComparison.Ordinal) || name.Contains("More", StringComparison.Ordinal) || name.Contains("SettingsButton", StringComparison.Ordinal)) return InteractionCue.Menu;
        return InteractionCue.Click;
    }

    private static DependencyObject? Parent(DependencyObject node) => node is Visual or Visual3D
        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
