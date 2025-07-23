// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Net.BuildServerUtils;

internal static class BuildServerUtility
{
    public const string DotNetHostServerPath = "DOTNET_HOST_SERVER_PATH";

    #region Server side

    public static void ListenForShutdown(Action onShutdown, Action<Exception> onError, CancellationToken cancellationToken)
    {
        Task.Run(async () =>
        {
            try
            {
                await WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
                onShutdown();
            }
            catch (OperationCanceledException e) when (e.CancellationToken == cancellationToken)
            {
                // Not an error.
            }
            catch (Exception ex)
            {
                onError(ex);
            }
        },
        cancellationToken);
    }

    public static async Task WaitForShutdownAsync(CancellationToken cancellationToken)
    {
        var pipePath = GetPipePath();

        // Delete the pipe if it exists (can happen if a previous build server did not shut down gracefully and its PID is recycled).
        File.Delete(pipePath);

        // Wait for any input which means shutdown is requested.
        using var server = new NamedPipeServerStream(pipePath);
        await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        await server.ReadAsync(new byte[1], 0, 1, cancellationToken).ConfigureAwait(false);

        // Close and delete the pipe.
        server.Dispose();
        File.Delete(pipePath);
    }

    private static string GetPipePath()
    {
        var folder = Environment.GetEnvironmentVariable(DotNetHostServerPath);

        if (string.IsNullOrEmpty(folder))
        {
            throw new InvalidOperationException($"Environment variable '{DotNetHostServerPath}' is not set.");
        }

        var pid = GetCurrentProcessId();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            folder = folder.Replace('/', '\\').Trim('\\').ToLowerInvariant();
            return $"""\\.\pipe\{folder}\{pid}""";
        }

        return Path.Combine(folder, $"{pid}.pipe");
    }

    private static int GetCurrentProcessId()
    {
#if NET
        return Environment.ProcessId;
#else
        return Process.GetCurrentProcess().Id;
#endif
    }

    #endregion

    #region Client side

#if NET

    public static Task ShutdownServersAsync(Action<Process> onProcessShutdownBegin, Action<string> onError, string hostServerPath)
    {
        // Enumerate pipes.
        return Task.WhenAll(Directory.EnumerateFiles(hostServerPath).Select(async file =>
        {
            try
            {
                // Try to parse PID from the file name.
                var pid = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(pid, out var processId))
                {
                    onError($"Cannot parse pipe file name: {file}");
                    return;
                }

                // Find the process.
                using var process = Process.GetProcessById(processId);
                onProcessShutdownBegin(process);

                // Connect to each pipe.
                var client = new NamedPipeClientStream(file);
                await using var _ = client.ConfigureAwait(false);
                await client.ConnectAsync().ConfigureAwait(false);

                // Send data to request shutdown.
                byte[] data = [1];
                await client.WriteAsync(data).ConfigureAwait(false);

                // Wait for the process to exit.
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onError($"Error while shutting down server for pipe '{file}': {ex.Message}");
            }
        }));
    }

#endif

    #endregion
}
