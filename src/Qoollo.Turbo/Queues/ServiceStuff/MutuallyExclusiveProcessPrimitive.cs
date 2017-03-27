﻿using Qoollo.Turbo.Threading.ServiceStuff;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Qoollo.Turbo.Queues.ServiceStuff
{
    /// <summary>
    /// Guard primitive for MutuallyExclusiveProcessPrimitive that allows to use it with 'using' statement
    /// </summary>
    internal struct MutuallyExclusiveProcessGuard : IDisposable
    {
        private MutuallyExclusiveProcessGate _srcGate;

        internal MutuallyExclusiveProcessGuard(MutuallyExclusiveProcessGate srcGate)
        {
            _srcGate = srcGate;
        }

        /// <summary>
        /// Is entering the protected section was successful
        /// </summary>
        public bool IsAcquired
        {
            get { return _srcGate != null; }
        }

        /// <summary>
        /// Gets the token to cancel the waiting procedure
        /// </summary>
        public CancellationToken Token
        {
            get
            {
                return _srcGate != null ? _srcGate.Token : new CancellationToken(true);
            }
        }

        /// <summary>
        /// Exits the protected code section
        /// </summary>
        public void Dispose()
        {
            if (_srcGate != null)
            {
                _srcGate.ExitClient();
                _srcGate = null;
            }
        }
    }

    /// <summary>
    /// Tracks the clients and notifies when closed and all clients exited
    /// </summary>
    internal class MutuallyExclusiveProcessGate : IDisposable
    {
        private readonly Action _clientsExitedNotification;
        private readonly ManualResetEventSlim _event;
        private volatile CancellationTokenSource _cancellationRequest;
        private volatile int _currentCountInner;
        private volatile bool _isDisposed;


        public MutuallyExclusiveProcessGate(bool opened, Action clientsExitedNotification)
        {
            if (opened)
            {
                _currentCountInner = 1;
                _clientsExitedNotification = clientsExitedNotification;
                _event = new ManualResetEventSlim(true);
                _cancellationRequest = new CancellationTokenSource();
            }
            else
            {
                _currentCountInner = 0;
                _clientsExitedNotification = clientsExitedNotification;
                _event = new ManualResetEventSlim(false);
                _cancellationRequest = new CancellationTokenSource();
                _cancellationRequest.Cancel();
            }
        }

        /// <summary>
        /// The current number of entered clients
        /// </summary>
        public int CurrentCount { get { return Math.Max(0, _currentCountInner - 1); } }
        /// <summary>
        /// Is gate opened
        /// </summary>
        public bool IsOpened { get { return _event.IsSet; } }
        /// <summary>
        /// Is all clients exited
        /// </summary>
        public bool IsFullyClosed { get { return !_event.IsSet && _currentCountInner <= 0; } }
        /// <summary>
        /// Token to cancel processes depends on this gate
        /// </summary>
        public CancellationToken Token { get { return _cancellationRequest.Token; } }


        private bool TryEnterDoubleCheck()
        {
            int newCount = Interlocked.Increment(ref _currentCountInner);
            Debug.Assert(newCount > 0);
            if (_event.IsSet)
                return true;

            int newCountDec = Interlocked.Decrement(ref _currentCountInner);
            if (newCount > 1 && newCountDec == 0)
                ExitClientAdditionalActions(newCountDec);

            return false;
        }
        /// <summary>
        /// Attempts to pass the gate if it is open
        /// </summary>
        public MutuallyExclusiveProcessGuard EnterClient(int timeout, CancellationToken token)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(this.GetType().Name);
            if (token.IsCancellationRequested)
                throw new OperationCanceledException(token);

            uint startTime = 0;
            if (timeout > 0)
                startTime = TimeoutHelper.GetTimestamp();
            else if (timeout < -1)
                timeout = Timeout.Infinite;

            if (_event.Wait(0) && TryEnterDoubleCheck())
                return new MutuallyExclusiveProcessGuard(this);

            if (timeout != 0)
            {
                while (!_isDisposed)
                {
                    int remainingWaitMilliseconds = Timeout.Infinite;
                    if (timeout != Timeout.Infinite)
                    {
                        remainingWaitMilliseconds = TimeoutHelper.UpdateTimeout(startTime, timeout);
                        if (remainingWaitMilliseconds <= 0)
                            return new MutuallyExclusiveProcessGuard();
                    }

                    if (_event.Wait(remainingWaitMilliseconds, token) && !_isDisposed && TryEnterDoubleCheck())
                        return new MutuallyExclusiveProcessGuard(this);
                }

                if (_isDisposed)
                    throw new ObjectDisposedException(this.GetType().Name);
            }

            return new MutuallyExclusiveProcessGuard();
        }


        private void ExitClientAdditionalActions(int newCount)
        {
            Debug.Assert(newCount == 0);

            if (!_isDisposed && !_event.IsSet && _clientsExitedNotification != null)
                _clientsExitedNotification();
        }

        internal void ExitClient()
        {
            int newCount = Interlocked.Decrement(ref _currentCountInner);

            Debug.Assert(newCount >= 0);
            if (newCount == 0)
                ExitClientAdditionalActions(newCount);
        }

        /// <summary>
        /// Close gate
        /// </summary>
        public void Close()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(this.GetType().Name);

            if (_event.IsSet)
            {
                _event.Reset();
                CancellationTokenSource srcToCancel = _cancellationRequest;
                ExitClient(); // This can request reopen          
                srcToCancel.Cancel(); // Should cancel after ExitClient    
            }
        }
        /// <summary>
        /// Reopen gate
        /// </summary>
        public bool Open()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(this.GetType().Name);

            if (!_event.IsSet)
            {
                int numberOfClients = Interlocked.Increment(ref _currentCountInner);
                Debug.Assert(numberOfClients > 0);
                _cancellationRequest = new CancellationTokenSource();
                _event.Set();

                return true;
            }

            return false;
        }

        /// <summary>
        /// Cleans-up resources
        /// </summary>
        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _event.Set();
                _event.Dispose();
                _cancellationRequest.Cancel();
                _cancellationRequest.Dispose();
            }
        }
    }

    /// <summary>
    /// Allows clients only for one gate from two to perform processing
    /// </summary>
    internal class MutuallyExclusiveProcessPrimitive : IDisposable
    {
        private readonly MutuallyExclusiveProcessGate _gate1;
        private readonly MutuallyExclusiveProcessGate _gate2;
        private readonly object _syncObj;
        private volatile int _forceGate1Waiter;
        private volatile bool _isDisposed;


        public MutuallyExclusiveProcessPrimitive(bool gate1Opened)
        {
            _syncObj = new object();
            _gate1 = new MutuallyExclusiveProcessGate(gate1Opened, Gate1Closed);
            _gate2 = new MutuallyExclusiveProcessGate(!gate1Opened, Gate2Closed);
        }
        public MutuallyExclusiveProcessPrimitive() : this(true)
        {     
        }

        [Conditional("DEBUG")]
        private void ValidateState()
        {
            SpinWait sw = new SpinWait();
            while (!_isDisposed && _gate1.IsFullyClosed && _gate2.IsFullyClosed)
            {
                sw.SpinOnce();
                if (sw.Count > 200)
                    Debug.Fail("Invalid MutuallyExclusiveProcessPrimitive state. At least one gate should be opened");
            }

            sw.Reset();
            while (!_gate1.IsFullyClosed && !_gate2.IsFullyClosed)
            {
                sw.SpinOnce();
                if (sw.Count > 20)
                    Debug.Fail("Both gates opened");
            }
        }

        private void Gate1Closed()
        {
            Debug.Assert(!_gate1.IsOpened, "gate1 is not closed");
            Debug.Assert(!_gate2.IsOpened, "gate2 is not closed");
            if (!_isDisposed)
            {
                lock (_syncObj)
                {
                    if (!_isDisposed)
                    {
                        if (_forceGate1Waiter == 0)
                        {
                            bool opened = _gate2.Open();
                            Debug.Assert(opened, "gate2 open failed. It's state = " + _gate2.IsOpened);
                        }
                        else
                        {
                            Debug.Assert(_gate2.IsFullyClosed, "gate2 is not fully closed");
                            bool opened = _gate1.Open();
                            Debug.Assert(opened, "gate1 open failed");
                        }
                    }
                }
            }
        }
        private void Gate2Closed()
        {
            Debug.Assert(!_gate1.IsOpened, "gate1 is not closed");
            Debug.Assert(!_gate2.IsOpened, "gate2 is not closed");
            if (!_isDisposed)
            {
                lock (_syncObj)
                {
                    if (!_isDisposed)
                    {
                        bool opened = _gate1.Open();
                        Debug.Assert(opened, "gate1 open failed");
                    }
                }
            }
        }

        /// <summary>
        /// Request Gate 1 to be opened and Gate 2 to be closed
        /// </summary>
        public void RequestGate1Open()
        {
            ValidateState();
            if (_gate2.IsOpened)
            {
                lock (_syncObj)
                {
                    _gate2.Close();
                }
            }
        }
        /// <summary>
        /// Request Gate 2 to be opened and Gate 1 to be closed
        /// </summary>
        public void RequestGate2Open()
        {
            ValidateState();
            if (_forceGate1Waiter == 0 && _gate1.IsOpened)
            {
                lock (_syncObj)
                {
                    if (_forceGate1Waiter == 0)
                        _gate1.Close();
                }
            }
        }

        /// <summary>
        /// Attempts to pass the gate 1 if it is open
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MutuallyExclusiveProcessGuard EnterGate1(int timeout, CancellationToken token)
        {
            ValidateState();
            return _gate1.EnterClient(timeout, token);
        }
        /// <summary>
        /// Open gate 1 and attempt to pass it
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MutuallyExclusiveProcessGuard OpenAndEnterGate1(int timeout, CancellationToken token)
        {
            ValidateState();
            try
            {
                Interlocked.Increment(ref _forceGate1Waiter);
                lock (_syncObj)
                {            
                    _gate2.Close();
                }
                return _gate1.EnterClient(timeout, token);
            }
            finally
            {
                Interlocked.Decrement(ref _forceGate1Waiter);
            }
        }
        /// <summary>
        /// Attempts to pass the gate 2 if it is open
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public MutuallyExclusiveProcessGuard EnterGate2(int timeout, CancellationToken token)
        {
            ValidateState();
            return _gate2.EnterClient(timeout, token);
        }


        /// <summary>
        /// Cleans-up resources
        /// </summary>
        public void Dispose()
        {
            if (!_isDisposed)
            {
                lock (_syncObj)
                {
                    if (!_isDisposed)
                    {
                        _isDisposed = true;
                        _gate1.Dispose();
                        _gate2.Dispose();
                    }
                }
            }
        }
    }
}
