using Concentus.Enums;
using Concentus.Structs;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

class Program
{
    delegate void AudioCallback(IntPtr data, int length);

    [DllImport("ApplicationLoopback.dll", CallingConvention = CallingConvention.StdCall)]
    static extern void SetAudioCallback(AudioCallback callback);

    [DllImport("ApplicationLoopback.dll", CallingConvention = CallingConvention.StdCall)]
    static extern IntPtr StartCaptureAsync(uint processId, bool includeProcessTree, ushort channel, uint sampleRate, ushort bitsPerSample);

    [DllImport("ApplicationLoopback.dll", CallingConvention = CallingConvention.StdCall)]
    static extern int StopCaptureAsync();

    static NamedPipeServerStream pipeServer;
    static OpusEncoder encoder;

    // Configurações para 48kHz Mono
    const int sampleRate = 48000;
    const int channels = 1;
    const int frameSizeSamples = 960; // 20ms de áudio a 48kHz
    const int pcmFrameSizeBytes = frameSizeSamples * sizeof(short); // 1920 bytes por quadro

    static List<byte> pcmAccumulator = new List<byte>();
    static object lockObj = new object();
    static AudioCallback audioCallback = OnAudioReceived;

    static void OnAudioReceived(IntPtr data, int length)
    {
        if (length <= 0 || pipeServer == null || !pipeServer.IsConnected) return;

        byte[] incomingBytes = new byte[length];
        Marshal.Copy(data, incomingBytes, 0, length);

        lock (lockObj)
        {
            if (data == null || incomingBytes == null) return;

            // 1. Acumula os bytes que chegam da DLL
            pcmAccumulator.AddRange(incomingBytes);

            // 2. Processa em blocos exatos de 20ms (1920 bytes) exigidos pelo Opus
            while (pcmAccumulator.Count >= pcmFrameSizeBytes)
            {
                byte[] rawPcmFrame = pcmAccumulator.GetRange(0, pcmFrameSizeBytes).ToArray();
                pcmAccumulator.RemoveRange(0, pcmFrameSizeBytes);

                // Converter bytes PCM16 para array de shorts
                short[] shortSamples = new short[frameSizeSamples];
                Buffer.BlockCopy(rawPcmFrame, 0, shortSamples, 0, pcmFrameSizeBytes);

                // Buffer de saída para o áudio comprimido (máximo de 1275 bytes por frame Opus)
                byte[] encodedBuffer = new byte[1275];
                int encodedLength = encoder.Encode(shortSamples, 0, frameSizeSamples, encodedBuffer, 0, encodedBuffer.Length);

                // 3. Monta o pacote: [4 bytes com tamanho do Opus + bytes do Opus]
                byte[] packet = new byte[4 + encodedLength];
                byte[] lengthBytes = BitConverter.GetBytes(encodedLength);
                Buffer.BlockCopy(lengthBytes, 0, packet, 0, 4);
                Buffer.BlockCopy(encodedBuffer, 0, packet, 4, encodedLength);

                try
                {
                    pipeServer.Write(packet, 0, packet.Length);
                    pipeServer.Flush();
                }
                catch
                {
                    break;
                }
            }
        }
    }

    static void Main(string[] args)
    {
        if(args.Length == 0)
        {
            Console.WriteLine("Se você estiver iniciando o Audio Capture para validar seu uso. Deu tudo certo!");
            Console.WriteLine("Agora pode fechar este CMD");
            Console.ReadLine();
            return;
        }
        Console.WriteLine("NÃO FECHE ESTE CMD, ELE ESTÁ CAPTANDO O AUDIO DOS PROCESSOS DO SEU COMPUTADOR!!!!");

        uint pid = args.Length > 0 ? uint.Parse(args[0]) : 12345;
        string pipeName = $"AudioCapturePipe_{pid}";

        bool FullAudio = args.Length > 1 ? bool.Parse(args[1]) : false;
        // Criar o Encoder Opus (48kHz, Mono, Otimizado para VoIP/Baixa Latência)
        encoder = new OpusEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 48000; // Taxa de transferência: 48 kbps (Reduz em ~90% o uso da rede)

        pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.WriteThrough);

        pipeServer.WaitForConnection();

        SetAudioCallback(audioCallback);

        // Captura nativa a 48000Hz em Mono
        StartCaptureAsync(pid, FullAudio, (ushort)channels, (uint)sampleRate, 16);

        Console.ReadLine();
    }
}