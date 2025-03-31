// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.IO.Compression
{
    internal sealed partial class ZLibStreamHandlePool<TState>
    {
        private static readonly uint ManagedPoolSize = (uint)Environment.ProcessorCount;

        private readonly ZLibNative.ZLibStreamHandle?[] _managedHandles;
        private readonly TState _state;
        private readonly Func<TState, ZLibNative.ZLibStreamHandle> _handleCreationFunction;

        private static uint s_nextThreadIndex;

        [ThreadStatic]
        private static uint s_currIndex;
        [ThreadStatic]
        private static bool s_indexesInitialized;

        [ThreadStatic]
        private static uint _rollingIndex;

        public ZLibStreamHandlePool(TState state, Func<TState, ZLibNative.ZLibStreamHandle> handleCreationFunction)
        {
            _managedHandles = new ZLibNative.ZLibStreamHandle?[ManagedPoolSize];
            _state = state;
            _handleCreationFunction = handleCreationFunction;
        }

        private static void GenerateCurrentIndex()
        {
            if (!s_indexesInitialized)
            {
                uint nextIdx = Interlocked.Increment(ref s_nextThreadIndex) - 1;

                s_currIndex = nextIdx % ManagedPoolSize;
                s_indexesInitialized = true;
            }
        }

        public ZLibNative.ZLibStreamHandle Rent()
        {
            ZLibNative.ZLibStreamHandle? handle;

            GenerateCurrentIndex();
            handle = TryGetHandle(s_currIndex)
                ?? TryGetHandle(_rollingIndex++ % ManagedPoolSize)
                ?? _handleCreationFunction(_state);

            return handle!;
        }

        private ZLibNative.ZLibStreamHandle? TryGetHandle(uint index)
        {
            ref ZLibNative.ZLibStreamHandle? handleRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_managedHandles), index % ManagedPoolSize);
            ZLibNative.ZLibStreamHandle? handle = Interlocked.Exchange(ref handleRef, null);

            return handle;
        }

        public void Return(ZLibNative.ZLibStreamHandle handle)
        {
            GenerateCurrentIndex();

            bool returned = TryReturnHandle(handle, s_currIndex)
                || TryReturnHandle(handle, _rollingIndex++);

            if (!returned)
            {
                handle.Dispose();
            }
        }

        private bool TryReturnHandle(ZLibNative.ZLibStreamHandle handle, uint idx)
        {
            // Try to reset and return the handle. If the handle can't be reset for some reason, then mark it for disposal.
            ZLibNative.ErrorCode errC = handle.Reset();

            if (errC == ZLibNative.ErrorCode.Ok)
            {
                handle.AvailOut = 0;
                handle.AvailIn = 0;
                handle.NextIn = 0;
                handle.NextOut = 0;

                ref ZLibNative.ZLibStreamHandle? handleRef = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_managedHandles), idx % ManagedPoolSize);
                ZLibNative.ZLibStreamHandle? previousHandle = Interlocked.CompareExchange(ref handleRef, handle, null);

                // Returns true if the handle has been successfully returned
                return previousHandle is null;
            }
            else
            {
                return false;
            }
        }
    }
}
