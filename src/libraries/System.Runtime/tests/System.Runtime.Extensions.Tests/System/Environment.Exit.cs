// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO;
using Microsoft.DotNet.RemoteExecutor;
using Xunit;

namespace System.Tests
{
    public class Environment_Exit
    {
        public static object[][] ExitCodeValues = new object[][]
        {
            new object[] { 0 },
            new object[] { 1 },
            new object[] { 42 },
            new object[] { -1 },
            new object[] { -45 },
            new object[] { 255 },
        };

        [ConditionalTheory(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        [MemberData(nameof(ExitCodeValues))]
        public static void CheckExitCode(int expectedExitCode)
        {
            RemoteExecutor.Invoke(s => int.Parse(s), expectedExitCode.ToString(), new RemoteInvokeOptions { ExpectedExitCode = expectedExitCode }).Dispose();
        }

        [Theory]
        [MemberData(nameof(ExitCodeValues))]
        public static void ExitCode_Roundtrips(int exitCode)
        {
            Environment.ExitCode = exitCode;
            Assert.Equal(exitCode, Environment.ExitCode);

            Environment.ExitCode = 0; // in case the test host has a void returning Main
        }

        [ConditionalTheory(typeof(RemoteExecutor), nameof(RemoteExecutor.IsSupported))]
        [InlineData(1)] // setting ExitCode and exiting Main
        [InlineData(2)] // setting ExitCode both from Main and from an Unloading event handler.
        [InlineData(3)] // using Exit(exitCode)
        public static void ExitCode_VoidMainAppReturnsSetValue(int mode)
        {
            int expectedExitCode = 123;
            const string AppName = "VoidMainWithExitCodeApp.dll";

            using (Process p = Process.Start(RemoteExecutor.HostRunner, new[] { AppName, expectedExitCode.ToString(), mode.ToString() }))
            {
                p.WaitForExit();
                Assert.Equal(expectedExitCode, p.ExitCode);
            }
        }
    }
}
