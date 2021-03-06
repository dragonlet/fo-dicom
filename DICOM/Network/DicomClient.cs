﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;

using Dicom.Log;

namespace Dicom.Network {
	public class DicomClient {
		private EventAsyncResult _async;
		private Exception _exception;
		private List<DicomRequest> _requests;
		private List<DicomPresentationContext> _contexts;
		private DicomServiceUser _service;
		private int _asyncInvoked;
		private int _asyncPerformed;
		private TcpClient _client;
		private bool _abort;

		public DicomClient() {
			_requests = new List<DicomRequest>();
			_contexts = new List<DicomPresentationContext>();
			_asyncInvoked = 1;
			_asyncPerformed = 1;
			Linger = 50;
		}

		public void NegotiateAsyncOps(int invoked = 0, int performed = 0) {
			_asyncInvoked = invoked;
			_asyncPerformed = performed;
		}

		/// <summary>
		/// Time in milliseconds to keep connection alive for additional requests.
		/// </summary>
		public int Linger {
			get;
			set;
		}

		/// <summary>
		/// Logger that is passed to the underlying DicomService implementation.
		/// </summary>
		public Logger Logger {
			get;
			set;
		}

		/// <summary>
		/// Options to control behavior of <see cref="DicomService"/> base class.
		/// </summary>
		public DicomServiceOptions Options {
			get;
			set;
		}

		/// <summary>
		/// Additional presentation contexts to negotiate with association.
		/// </summary>
		public List<DicomPresentationContext> AdditionalPresentationContexts {
			get { return _contexts; }
			set { _contexts = value; }
		}

		public object UserState {
			get;
			set;
		}

		public void AddRequest(DicomRequest request) {
			if (_service != null && _service.IsConnected) {
				_service.SendRequest(request);
				if (_service._timer != null)
					_service._timer.Change(Timeout.Infinite, Timeout.Infinite);
			} else
				_requests.Add(request);
		}

		public void Send(string host, int port, bool useTls, string callingAe, string calledAe) {
			EndSend(BeginSend(host, port, useTls, callingAe, calledAe, null, null));
		}

		public IAsyncResult BeginSend(string host, int port, bool useTls, string callingAe, string calledAe, AsyncCallback callback, object state) {
			_client = new TcpClient(host, port);

			if (Options != null)
				_client.NoDelay = Options.TcpNoDelay;
			else
				_client.NoDelay = DicomServiceOptions.Default.TcpNoDelay;

			Stream stream = _client.GetStream();

			if (useTls) {
				var ssl = new SslStream(stream, false, ValidateServerCertificate);
				ssl.AuthenticateAsClient(host);
				stream = ssl;
			}

			return BeginSend(stream, callingAe, calledAe, callback, state);
		}

		public void Send(Stream stream, string callingAe, string calledAe) {
			EndSend(BeginSend(stream, callingAe, calledAe, null, null));
		}

		public IAsyncResult BeginSend(Stream stream, string callingAe, string calledAe, AsyncCallback callback, object state) {
			var assoc = new DicomAssociation(callingAe, calledAe);
			assoc.MaxAsyncOpsInvoked = _asyncInvoked;
			assoc.MaxAsyncOpsPerformed = _asyncPerformed;
			foreach (var request in _requests)
				assoc.PresentationContexts.AddFromRequest(request);
			foreach (var context in _contexts)
				assoc.PresentationContexts.Add(context.AbstractSyntax, context.GetTransferSyntaxes().ToArray());

			_service = new DicomServiceUser(this, stream, assoc, Logger);

			_async = new EventAsyncResult(callback, state);
			return _async;
		}

		private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors) {
			if (sslPolicyErrors == SslPolicyErrors.None)
				return true;

			if (Options != null) {
				if (Options.IgnoreSslPolicyErrors)
					return true;
			} else if (DicomServiceOptions.Default.IgnoreSslPolicyErrors)
				return true;

			return false;
		}

		public void EndSend(IAsyncResult result) {
			_async.AsyncWaitHandle.WaitOne();

			if (_client != null) {
				try {
					_client.Close();
				} catch {
				}
			}

			_service = null;
			_async = null;

			if (_exception != null && !_abort)
				throw _exception;
		}

		public void Abort() {
			try {
				_abort = true;
				_client.Close();
			} catch {
			}
		}

		private class DicomServiceUser : DicomService, IDicomServiceUser {
			public DicomClient _client;
			public Timer _timer;

			public DicomServiceUser(DicomClient client, Stream stream, DicomAssociation association, Logger log) : base(stream, log) {
				_client = client;
				if (_client.Options != null)
					Options = _client.Options;
				SendAssociationRequest(association);
			}

			public void OnReceiveAssociationAccept(DicomAssociation association) {
				foreach (var request in _client._requests)
					SendRequest(request);
				_client._requests.Clear();
			}

			protected override void OnSendQueueEmpty() {
				if (_client.Linger == Timeout.Infinite) {
					OnLingerTimeout(null);
				} else {
					_timer = new Timer(OnLingerTimeout);
					_timer.Change(_client.Linger, Timeout.Infinite);
				}
			}

			private void OnLingerTimeout(object state) {
				if (!IsSendQueueEmpty)
					return;

				try {
					SendAssociationReleaseRequest();
				} catch {
					// may have already disconnected
					_client._async.Set();
					return;
				}

				_timer = new Timer(OnReleaseTimeout);
				_timer.Change(2500, Timeout.Infinite);
			}

			private void OnReleaseTimeout(object state) {
				try {
					if (_client._async != null)
						_client._async.Set();
				} catch {
					// event handler has already fired
				}
			}

			public void OnReceiveAssociationReject(DicomRejectResult result, DicomRejectSource source, DicomRejectReason reason) {
				if (_timer != null)
					_timer.Change(Timeout.Infinite, Timeout.Infinite);

				_client._exception = new DicomAssociationRejectedException(result, source, reason);
				_client._async.Set();
			}

			public void OnReceiveAssociationReleaseResponse() {
				if (_timer != null)
					_timer.Change(Timeout.Infinite, Timeout.Infinite);

				_client._async.Set();
			}

			public void OnReceiveAbort(DicomAbortSource source, DicomAbortReason reason) {
				if (_timer != null)
					_timer.Change(Timeout.Infinite, Timeout.Infinite);

				_client._exception = new DicomAssociationAbortedException(source, reason);
				_client._async.Set();
			}

			public void OnConnectionClosed(int errorCode) {
				if (_timer != null)
					_timer.Change(Timeout.Infinite, Timeout.Infinite);

				if (errorCode != 0)
					_client._exception = new SocketException(errorCode);

				try {
					if (_client._async != null)
						_client._async.Set();
				} catch {
					// event handler has already fired
				}
			}
		}
	}
}
