using System.Diagnostics;
using NAudio.Wave;

namespace MicProbe;

/// <summary>
/// 麦克风采集诊断：列出设备、逐台录 2 秒、报告"到底有没有数据"。
///
/// 起因：PTT 按住 3 秒只收到 40 ms 音频（2 个 20 ms 块），日志里还有
/// "NoDriver calling waveInPrepareHeader"。需要区分是代码问题还是设备/驱动问题。
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("=== 麦克风诊断 ===");
        Console.WriteLine();

        var devices = WaveInEvent.DeviceCount;
        Console.WriteLine($"  录音设备数量：{devices}");

        for (var i = 0; i < devices; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            Console.WriteLine($"    [{i}] {caps.ProductName}（声道 {caps.Channels}）");
        }

        Console.WriteLine();

        if (devices == 0)
        {
            Console.WriteLine("  没有录音设备，PTT 无从采集。");
            return 1;
        }

        // 逐个设备录 2 秒，看谁真的出数据
        for (var index = 0; index < devices; index++)
        {
            var name = WaveInEvent.GetCapabilities(index).ProductName;
            Console.WriteLine($"  ---- 设备 [{index}] {name} ----");

            var chunks = 0;
            var samples = 0;
            double sumSquares = 0;
            Exception? error = null;
            var stoppedEarly = false;
            var sw = Stopwatch.StartNew();

            using var waveIn = new WaveInEvent
            {
                DeviceNumber = index,
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 20,
                NumberOfBuffers = 4,
            };

            waveIn.DataAvailable += (_, e) =>
            {
                chunks++;
                samples += e.BytesRecorded / 2;

                for (var i = 0; i < e.BytesRecorded / 2; i++)
                {
                    var v = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8)) / 32768.0;
                    sumSquares += v * v;
                }
            };

            waveIn.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    error = e.Exception;
                    stoppedEarly = true;
                }
            };

            try
            {
                waveIn.StartRecording();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    启动失败：{ex.Message}");
                Console.WriteLine();
                continue;
            }

            Thread.Sleep(2000);
            sw.Stop();

            try
            {
                waveIn.StopRecording();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    停止时异常：{ex.Message}");
            }

            Thread.Sleep(200);

            var rms = samples > 0 ? Math.Sqrt(sumSquares / samples) : 0;
            var ms = samples / 16.0;   // 16 kHz 单声道：1 ms = 16 个样本

            Console.WriteLine($"    实测时长：{ms:N0} ms（块 {chunks} 个，样本 {samples}）");
            Console.WriteLine($"    RMS：{rms:F5}" + (rms < 0.0005 ? "  ← 基本是静音" : "  ← 有信号"));

            if (error is not null)
            {
                Console.WriteLine($"    设备异常：{error.Message}");
            }

            if (ms < 1000)
            {
                Console.WriteLine($"    [异常] 2 秒只收到 {ms:N0} ms 数据" + (stoppedEarly ? "（设备提前停止）" : "（没有任何异常回调）"));
            }
            else
            {
                Console.WriteLine("    [正常] 持续供数据");
            }

            Console.WriteLine();
        }

        return 0;
    }
}