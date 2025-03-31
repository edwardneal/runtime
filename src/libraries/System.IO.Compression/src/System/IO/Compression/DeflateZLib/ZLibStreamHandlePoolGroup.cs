// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace System.IO.Compression
{
    internal sealed partial class ZLibStreamHandlePoolGroup
    {
        // There are three default settings for a ZLibStreamHandle:
        // * DeflateStream: WindowBits == -15
        // * GZipStream: WindowBits == 31
        // * ZLibStream: WindowBits == 15
        private const int InitialPoolGroupSize = 3;

        private readonly struct DeflateConfiguration
        {
            public readonly sbyte Level;
            public readonly sbyte WindowBits;
            public readonly sbyte MemLevel;
            public readonly byte Strategy;

            public DeflateConfiguration(ZLibNative.CompressionLevel level, int windowBits, int memLevel, ZLibNative.CompressionStrategy strategy)
            {
                Debug.Assert((int)level is >= sbyte.MinValue and <= sbyte.MaxValue);
                Debug.Assert(windowBits is >= sbyte.MinValue and <= sbyte.MaxValue);
                Debug.Assert(memLevel is >= sbyte.MinValue and <= sbyte.MaxValue);
                Debug.Assert((int)strategy is >= byte.MinValue and <= byte.MaxValue);

                Level = (sbyte)level;
                WindowBits = (sbyte)windowBits;
                MemLevel = (sbyte)memLevel;
                Strategy = (byte)strategy;
            }

            public uint ToToken()
                => (uint)(((byte)Level << 24)
                | ((byte)WindowBits << 16)
                | ((byte)MemLevel << 8)
                | Strategy);
        }

        public static ZLibStreamHandlePoolGroup Shared { get; } = new ZLibStreamHandlePoolGroup();

        private readonly Lock _poolGroupLock;
        private readonly Dictionary<uint, ZLibStreamHandlePool<DeflateConfiguration>> _deflatePools;
        private readonly Dictionary<int, ZLibStreamHandlePool<int>> _inflatePools;

        private ZLibStreamHandlePoolGroup()
        {
            _poolGroupLock = new Lock();
            _deflatePools = new Dictionary<uint, ZLibStreamHandlePool<DeflateConfiguration>>(InitialPoolGroupSize);
            _inflatePools = new Dictionary<int, ZLibStreamHandlePool<int>>(InitialPoolGroupSize);
        }

        private static ZLibNative.ZLibStreamHandle CreateDeflatingHandle(DeflateConfiguration config)
        {
            ZLibNative.ZLibStreamHandle handle = new ZLibNative.ZLibStreamHandle();
            ZLibNative.ErrorCode errC;

            try
            {
                errC = handle.DeflateInit2_((ZLibNative.CompressionLevel)config.Level, config.WindowBits, config.MemLevel, (ZLibNative.CompressionStrategy)config.Strategy);
            }
            catch (Exception cause)
            {
                handle.Dispose();
                throw new ZLibException(SR.ZLibErrorDLLLoadError, cause);
            }

            return errC switch
            {
                ZLibNative.ErrorCode.Ok => handle,
                ZLibNative.ErrorCode.MemError => throw new ZLibException(SR.ZLibErrorNotEnoughMemory, "deflateInit2_", (int)errC, handle.GetErrorMessage()),
                ZLibNative.ErrorCode.VersionError => throw new ZLibException(SR.ZLibErrorVersionMismatch, "deflateInit2_", (int)errC, handle.GetErrorMessage()),
                ZLibNative.ErrorCode.StreamError => throw new ZLibException(SR.ZLibErrorIncorrectInitParameters, "deflateInit2_", (int)errC, handle.GetErrorMessage()),
                _ => throw new ZLibException(SR.ZLibErrorUnexpected, "deflateInit2_", (int)errC, handle.GetErrorMessage())
            };
        }

        private static ZLibNative.ZLibStreamHandle CreateInflatingHandle(int windowBits)
        {
            ZLibNative.ZLibStreamHandle handle = new ZLibNative.ZLibStreamHandle();
            ZLibNative.ErrorCode errC;

            try
            {
                errC = handle.InflateInit2_(windowBits);
            }
            catch (Exception cause)
            {
                handle.Dispose();
                throw new ZLibException(SR.ZLibErrorDLLLoadError, cause);
            }

            return errC switch
            {
                ZLibNative.ErrorCode.Ok => handle,
                ZLibNative.ErrorCode.MemError => throw new ZLibException(SR.ZLibErrorNotEnoughMemory, "inflateInit2_", (int)errC, handle.GetErrorMessage()),
                ZLibNative.ErrorCode.VersionError => throw new ZLibException(SR.ZLibErrorVersionMismatch, "inflateInit2_", (int)errC, handle.GetErrorMessage()),
                ZLibNative.ErrorCode.StreamError => throw new ZLibException(SR.ZLibErrorIncorrectInitParameters, "inflateInit2_", (int)errC, handle.GetErrorMessage()),
                _ => throw new ZLibException(SR.ZLibErrorUnexpected, "inflateInit2_", (int)errC, handle.GetErrorMessage())
            };
        }

        public ZLibNative.ZLibStreamHandle RentZLibStreamForDeflate(ZLibNative.CompressionLevel level, int windowBits, int memLevel, ZLibNative.CompressionStrategy strategy,
            out uint poolToken)
        {
            DeflateConfiguration deflateConfiguration = new DeflateConfiguration(level, windowBits, memLevel, strategy);
            ZLibStreamHandlePool<DeflateConfiguration>? deflationPool;

            poolToken = deflateConfiguration.ToToken();
            lock (_poolGroupLock)
            {
                if (!_deflatePools.TryGetValue(poolToken, out deflationPool))
                {
                    deflationPool = new ZLibStreamHandlePool<DeflateConfiguration>(deflateConfiguration, CreateDeflatingHandle);
                    _deflatePools.Add(poolToken, deflationPool);
                }
            }

            return deflationPool.Rent();
        }

        public ZLibNative.ZLibStreamHandle RentZLibStreamForInflate(int windowBits)
        {
            ZLibStreamHandlePool<int>? inflationPool;

            lock (_poolGroupLock)
            {
                if (!_inflatePools.TryGetValue(windowBits, out inflationPool))
                {
                    inflationPool = new ZLibStreamHandlePool<int>(windowBits, CreateInflatingHandle);
                    _inflatePools.Add(windowBits, inflationPool);
                }
            }

            return inflationPool.Rent();
        }

        public void ReturnZLibStreamForDeflate(ZLibNative.ZLibStreamHandle handle, uint poolToken)
        {
            ZLibStreamHandlePool<DeflateConfiguration> deflationPool;

            lock (_poolGroupLock)
            {
                deflationPool = _deflatePools[poolToken];
            }

            deflationPool.Return(handle);
        }

        public void ReturnZLibStreamForInflate(ZLibNative.ZLibStreamHandle handle, int windowBits)
        {
            int poolGroupKey = windowBits;
            ZLibStreamHandlePool<int>? inflationPool;

            lock (_poolGroupLock)
            {
                inflationPool = _inflatePools[poolGroupKey];
            }

            inflationPool.Return(handle);
        }
    }
}
