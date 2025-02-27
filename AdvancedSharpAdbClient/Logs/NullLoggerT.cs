﻿#if NET452
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace AdvancedSharpAdbClient.Logs
{
    using Microsoft.Extensions.Logging;
    using System;

    /// <summary>
    /// Minimalistic logger that does nothing.
    /// </summary>
    public class NullLogger<T> : ILogger<T>
    {
        /// <summary>
        /// Returns an instance of <see cref="NullLogger{T}"/>.
        /// </summary>
        /// <returns>An instance of <see cref="NullLogger{T}"/>.</returns>
        public static readonly NullLogger<T> Instance = new NullLogger<T>();

        /// <inheritdoc />
        public IDisposable BeginScope<TState>(TState state)
        {
            return NullScope.Instance;
        }

        /// <inheritdoc />
        /// <remarks>
        /// This method ignores the parameters and does nothing.
        /// </remarks>
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel)
        {
            return false;
        }
    }

    internal sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new NullScope();

        private NullScope()
        {
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
#endif
