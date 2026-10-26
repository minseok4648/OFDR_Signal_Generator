using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace OFDR_Signal_Core.Processing
{
    public sealed class FftEngine
    {
        public const int LargeDataThreshold = 1024 * 1024;
        private readonly object transformLock = new object();

        public FftEngine()
        {
            try
            {
                NativeProviderAvailable = Control.TryUseNativeMKL();
                if (!NativeProviderAvailable) NativeProviderFailure = "Control.TryUseNativeMKL() returned false.";
            }
            catch (Exception ex)
            {
                NativeProviderAvailable = false;
                NativeProviderFailure = ex.GetType().Name + ": " + ex.Message;
                Control.UseManaged();
            }
            ProviderDescription = Control.Describe();
        }

        public bool NativeProviderAvailable { get; private set; }
        public string NativeProviderFailure { get; private set; }
        public string ProviderDescription { get; private set; }

        public void EnsureSupportedLength(int length)
        {
            if (length > LargeDataThreshold && !NativeProviderAvailable)
            {
                var applicationFolder = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? AppDomain.CurrentDomain.BaseDirectory;
                var providerPath = Path.Combine(applicationFolder, "MathNet.Numerics.Providers.MKL.dll");
                var nativePath = Path.Combine(applicationFolder, "libMathNetNumericsMKL.dll");
                throw new InvalidOperationException(
                    "1,048,576 point를 초과하는 데이터에는 Native MKL FFT provider가 필요합니다. " +
                    "프로그램 폴더의 MKL 배포 상태를 확인하세요. " +
                    "Process=" + (Environment.Is64BitProcess ? "x64" : "x86") +
                    ", ProviderDll=" + (File.Exists(providerPath) ? "있음" : "없음") +
                    ", NativeDll=" + (File.Exists(nativePath) ? "있음" : "없음") +
                    ", 초기화=" + (NativeProviderFailure ?? "알 수 없음") + ".");
            }
        }

        public void ForwardInPlace(Complex[] samples)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            EnsureSupportedLength(samples.Length);
            lock (transformLock) Fourier.Forward(samples, FourierOptions.Matlab);
        }

        public void InverseInPlace(Complex[] samples)
        {
            if (samples == null) throw new ArgumentNullException(nameof(samples));
            EnsureSupportedLength(samples.Length);
            lock (transformLock) Fourier.Inverse(samples, FourierOptions.Matlab);
        }
    }

    internal static class Radix2Fft
    {
        public static void ForwardInPlace(Complex[] buffer)
        {
            Transform(buffer, false);
        }

        public static void InverseInPlace(Complex[] buffer)
        {
            Transform(buffer, true);
        }

        private static void Transform(Complex[] buffer, bool inverse)
        {
            var n = buffer == null ? 0 : buffer.Length;
            if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("Radix-2 FFT length must be a power of two.");
            for (int i = 1, j = 0; i < n; i++)
            {
                var bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { var temp = buffer[i]; buffer[i] = buffer[j]; buffer[j] = temp; }
            }
            for (var length = 2; length <= n; length <<= 1)
            {
                var angle = (inverse ? 2.0 : -2.0) * Math.PI / length;
                var root = new Complex(Math.Cos(angle), Math.Sin(angle));
                for (var start = 0; start < n; start += length)
                {
                    var factor = Complex.One;
                    var half = length >> 1;
                    for (var offset = 0; offset < half; offset++)
                    {
                        var even = buffer[start + offset];
                        var odd = buffer[start + offset + half] * factor;
                        buffer[start + offset] = even + odd;
                        buffer[start + offset + half] = even - odd;
                        factor *= root;
                    }
                }
            }
            if (inverse) for (var i = 0; i < n; i++) buffer[i] /= n;
        }

        public static int NextPowerOfTwo(int value)
        {
            var result = 1;
            while (result < value)
            {
                if (result > (1 << 29)) throw new ArgumentOutOfRangeException(nameof(value));
                result <<= 1;
            }
            return result;
        }
    }
}
