using Aetheros.OneM2M.Api.Registration;
using Aetheros.Schema.OneM2M;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace Aetheros.OneM2M.Api
{
	public class ApplicationConfiguration
	{
		public string? AppId { get; set; }
		public string? AppName { get; set; }
		public string? CredentialId { get; set; }
		public Uri? PoaUrl { get; set; }

		public string CseId { get; set; } = "/PN_CSE";
	}

	public class Application<TPrimitiveContent>
		where TPrimitiveContent : PrimitiveContent, new()
	{
		public Connection<TPrimitiveContent> Connection { get; }
		public AE Ae { get; }
		public string AeId => Ae.AE_ID;
		public string CseId { get; }

		public Application(Connection<TPrimitiveContent> con, AE ae, string urlPrefix)
		{
			Connection = con;
			Ae = ae;
			CseId = urlPrefix;
		}

		public string ToAbsolute(string key)
		{
			if (string.IsNullOrWhiteSpace(key))
				return $"{CseId}/{Ae.ResourceName}";
			if (key.StartsWith("/")) {
				if (key.StartsWith(CseId))
					return key;
				return $"{CseId}{key}";
			}
			return $"{CseId}/{Ae.ResourceName}/{key}";
		}


		public async Task<T> GetResponseAsync<T>(RequestPrimitive<TPrimitiveContent> body)
			where T : class, new()
		{
			body.To = ToAbsolute(body.To);
			if (body.From == null)
				body.From = AeId;
			return await Connection.GetResponseAsync<T>(body);
		}

		public async Task<ResponseContent<TPrimitiveContent>> GetResponseAsync(RequestPrimitive<TPrimitiveContent> body) => await GetResponseAsync<ResponseContent<TPrimitiveContent>>(body);

		public async Task<T> GetChildResourcesAsync<T>(string key, FilterCriteria? filterCriteria = null)
			where T : class, new() =>
			await GetResponseAsync<T>(new RequestPrimitive<TPrimitiveContent>
			{
				Operation = Operation.Retrieve,
				To = key,
				ResultContent = ResultContent.ChildResources,
				FilterCriteria = filterCriteria
			});

		public async Task<ResponseContent<TPrimitiveContent>> GetPrimitiveAsync(
			string key,
			FilterCriteria? filterCriteria = null,
			ResultContent? resultContent = null,
			DiscResType? discoveryResultType = null
		) =>
			await GetResponseAsync(new RequestPrimitive<TPrimitiveContent>
			{
				From = this.AeId,
				To = key,
				ResultContent = resultContent,
				Operation = Operation.Retrieve,
				FilterCriteria = filterCriteria,
				DiscoveryResultType = discoveryResultType,
			});

		public async Task<ResponseContent<TPrimitiveContent>> TryGetPrimitiveAsync(string url) => await Connection.TryGetPrimitiveAsync(this.AeId, ToAbsolute(url));

		public async Task<ResponseContent<TPrimitiveContent>> CreateResourceAsync(string url, ResourceType resourceType, Func<TPrimitiveContent, TPrimitiveContent> setter, ResultContent? resultContent = null) =>
			await GetResponseAsync(new RequestPrimitive<TPrimitiveContent>
			{
				From = this.AeId,
				To = url,
				Operation = Operation.Create,
				ResourceType = resourceType,
				ResultContent = resultContent,
				PrimitiveContent = setter(new TPrimitiveContent())
			});

		public async Task<ResponseContent<TPrimitiveContent>> UpdateResourceAsync(string url, Func<TPrimitiveContent, TPrimitiveContent> setter) =>
			await GetResponseAsync(new RequestPrimitive<TPrimitiveContent>
			{
				From = this.AeId,
				To = url,
				Operation = Operation.Update,
				PrimitiveContent = setter(new TPrimitiveContent())
			});


		public async Task DeleteAsync(params string[] urls) => await DeleteAsync((IEnumerable<string>) urls);

		public async Task DeleteAsync(IEnumerable<string> urls)
		{
			foreach (var url in urls)
			{
				try
				{
					await GetResponseAsync(new RequestPrimitive<TPrimitiveContent>
					{
						Operation = Operation.Delete,
						To = url,
					});
				}
				catch (OneM2MException e) when (e.StatusCode == ResponseStatusCode.NotFound)
				{
					// ignore
				}
			}
		}

		public async Task<ContentInstance> AddContentInstanceAsync(string key, object content) => await AddContentInstanceAsync(key, null, content);

		public async Task<ContentInstance> AddContentInstanceAsync(string key, string? resourceName, object content) =>
			(await this.GetResponseAsync(new RequestPrimitive<TPrimitiveContent>
			{
				To = key,
				Operation = Operation.Create,
				ResourceType = ResourceType.ContentInstance,
				PrimitiveContent = new TPrimitiveContent
				{
					ContentInstance = new ContentInstance
					{
						ResourceName = resourceName,
						Content = content
					}
				}
			})).ContentInstance;

		public async Task<Container?> EnsureContainerAsync(string name, string? aclUri = null)
		{
			if (name == "." || name == "/")
				return null;

			Trace.WriteLine($"Looking for container '{name}'");
			var prim = await TryGetPrimitiveAsync(name);

			// If the primitive exists and is a container, we try adding the AccessControlPolicy to it and/or return it
			var container = prim?.Container;;
			if (container != null)
			{
				if (aclUri != null)
				{
					if (container.AccessControlPolicyIDs == null || !container.AccessControlPolicyIDs.Contains(aclUri))
					{
						var accessControlPolicyIDs = container.AccessControlPolicyIDs?.ToList() ?? [];
						accessControlPolicyIDs.Add(aclUri);

						Trace.WriteLine($"Adding ACL '{aclUri}' to container '{name}'");
						container = (await UpdateResourceAsync(name, pc =>
						{
							pc.Container = new Container
							{
								AccessControlPolicyIDs = accessControlPolicyIDs
							};
							return pc;
						})).Container;
					}
				}
				return container;
			}
			Trace.WriteLine($"Did not find container '{name}'... ");

			// If the primitive exists and is an AE, we do not need to recreate it.
			var primAE = prim?.AE;
			if (primAE != null)
			{
				// We don't need to return a container, we can just return null
				return null;
			}

			string parentName = "";

			int ichLast = name.LastIndexOf('/');
			if (ichLast > 0)
			{
				parentName = name.Substring(0, ichLast);
				name = name.Substring(ichLast + 1);
				if (!string.IsNullOrWhiteSpace(parentName) && parentName != ".")
					await EnsureContainerAsync(parentName);
			}

			Trace.WriteLine($"Creating new container '{name}'");
			return (await CreateResourceAsync(
				parentName,
				ResourceType.Container,
				pc =>
				{
					pc.Container = new Container
					{
						ResourceName = name,
					};
					if (!string.IsNullOrWhiteSpace(aclUri))
					{
						pc.Container.AccessControlPolicyIDs = [aclUri];
					}
					return pc;
				}
			)).Container;
		}

		public async Task<T?> GetLatestContentInstanceAsync<T>(string containerKey)
			where T : class
		{
			try
			{
				var response = await GetPrimitiveAsync(
					containerKey + "/latest",
					new FilterCriteria
					{
						ResourceType = [ResourceType.ContentInstance],
					}
				);
				return response.ContentInstance?.GetContent<T>();
			}
			catch (OneM2MException e) when (e.StatusCode == ResponseStatusCode.NotFound)
			{
				return null;
			}
		}

		/// <summary>
		/// Creates a subscription to receive notifications from the server.
		/// The subscription is created on the resource specified by resourceId.
		/// The subscription is identified by the subscriptionName.
		/// </summary>
		/// <param name="resourceId">the resource (eg. Container) to subscribe to</param>
		/// <param name="subscriptionName">the unique name of the subscription</param>
		/// <param name="criteria">allows filtering of notifications</param>
		/// <param name="poaUrl">overrides the AE's default POA URL</param>
		/// <param name="deleteAfterFinalClose">if this is true, the subscription will be deleted after the returned observable is disposed</param>
		/// <param name="batchSize">enables batching of notifications in a single request</param>
		/// <returns></returns>
		/// <exception cref="ProtocolViolationException"></exception>
		public async Task<IObservable<NotificationNotificationEvent<TPrimitiveContent>>> ObserveNotificationAsync(
			string resourceId,
			string subscriptionName,
			EventNotificationCriteria? criteria = null,
			string? poaUrl = null,
			bool deleteAfterFinalClose = false,
			int batchSize = 1)
		{
			var subscriptionReference = $"{ToAbsolute(resourceId)}/{subscriptionName}";
			var subscription = (await TryGetPrimitiveAsync(subscriptionReference))?.Subscription;

			//create subscription only if can't find subscription with the same notification url
			if (subscription != null)
			{
				Debug.WriteLine($"Using existing subscription {subscriptionReference}");
			}
			else
			{
				BatchNotify? batchNotify = (batchSize <= 1) ? null : new BatchNotify
				{
					Number = batchSize
				};

				var subscriptionResponse = await CreateResourceAsync(
					resourceId,
					ResourceType.Subscription,
					pc => {
						pc.Subscription = new Subscription
						{
							ResourceName = subscriptionName,
							EventNotificationCriteria = criteria ?? _defaultEventNotificationCriteria,
							NotificationContentType = NotificationContentType.AllAttributes,
							NotificationURI = [poaUrl ?? this.AeId],
							BatchNotify = batchNotify,
						};
						return pc;
					},
					resultContent: ResultContent.Attributes
				);

				subscription = subscriptionResponse.Subscription ?? throw new ProtocolViolationException("CreateResourceAsync succeeded but did not return a URI");
				Debug.Assert(subscription.ResourceName == subscriptionName);
				Debug.WriteLine($"Created Subscription {subscriptionReference}");
			}

			return Connection.Notifications
				.Where(n => n.SubscriptionReference == subscriptionReference)
				.Select(n => n.NotificationEvent)
				.Finally(() =>
				{
					if (deleteAfterFinalClose)
						DeleteAsync(subscriptionReference).Wait();
				})
				.Publish()
				.RefCount();
		}


		static readonly EventNotificationCriteria _defaultEventNotificationCriteria = new EventNotificationCriteria
		{
			NotificationEventType = [NotificationEventType.CreateChild],
		};

		public IObservable<TContent> FilterContentInstances<TContent>(IObservable<NotificationNotificationEvent<TPrimitiveContent>> observable)
			where TContent : class
		{
			return observable
				.Select(evt => evt.PrimitiveRepresentation)
				.WhereNotNull()
				.Select(pc => pc.ContentInstance?.GetContent<TContent>())
				.WhereNotNull();
		}

		public async Task<IObservable<TContent>> ObserveContentInstanceAsync<TContent>(
			string containerName,
			string subscriptionName,
			string? poaUrl = null,
			bool deleteAfterFinalClose = false,
			int batchSize = 1
			)
			where TContent : class
		{
			var container = await this.EnsureContainerAsync(containerName);
			var observable = await this.ObserveNotificationAsync(containerName, subscriptionName, poaUrl: poaUrl, deleteAfterFinalClose: deleteAfterFinalClose, batchSize: batchSize);
			return FilterContentInstances<TContent>(observable);
		}


		public static async Task<X509Certificate2> GenerateSigningCertificateAsync(Uri caUri, AE ae, string certificateFilename)
		{
			var tokenId = ae.Labels?.FirstOrDefault(l => l.StartsWith("token="))?.Substring("token=".Length);
			if (string.IsNullOrWhiteSpace(tokenId))
				throw new InvalidDataException("registered AE is missing 'token' label");

			var csrUri = new Uri(caUri, "CertificateSigning");
			var ccrUri = new Uri(caUri, "CertificateConfirm");

			using var privateKey = ECDsa.Create();
			var certificateRequest = new CertificateRequest(
				new X500DistinguishedName($"CN={ae.AE_ID}"),
				privateKey,
				HashAlgorithmName.SHA256);

			certificateRequest.CertificateExtensions.Add(
				new X509KeyUsageExtension(
					X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
					true));

			certificateRequest.CertificateExtensions.Add(
				new X509EnhancedKeyUsageExtension(
					[
						new Oid("1.3.6.1.5.5.7.3.1"), // serverAuth : TLS Web server authentication
						new Oid("1.3.6.1.5.5.7.3.2"), // clientAuth : TLS Web client authentication
						new Oid("1.3.6.1.5.5.7.3.3"), // codeSigning : Code signing
					],
					false));

			var sanBuilder = new SubjectAlternativeNameBuilder();
			//sanBuilder.AddDnsName(ae.App_ID);
			//sanBuilder.AddDnsName(ae.AE_ID);
			sanBuilder.AddUri(new Uri($"urn://policynetiot.com/{ae.AE_ID}"));
			sanBuilder.AddUri(new Uri($"urn:{ae.App_ID}"));
			certificateRequest.CertificateExtensions.Add(sanBuilder.Build());

			//Debug.WriteLine(certificateRequest.ToPemString());
			var signingRequest = new CertificateSigningRequestBody
			{
				Request = new CertificateSigningRequest
				{
					Application = new Aetheros.OneM2M.Api.Registration.Application
					{
						AeId = ae.AE_ID,
						TokenId = tokenId
					},
					X509Request = certificateRequest.ToPemString(X509SignatureGenerator.CreateForECDsa(privateKey)),
				}
			};


			var sslOptions = new SslClientAuthenticationOptions
			{
				RemoteCertificateValidationCallback = delegate { return true; },
				CipherSuitesPolicy = new CipherSuitesPolicy(Enum.GetValues<TlsCipherSuite>())
			};
			var socketsHttpHandler = new SocketsHttpHandler { SslOptions = sslOptions };			
			var loggingHandler = new TraceMessageHandler(socketsHttpHandler);

			using var client = new HttpClient(loggingHandler);

			CertificateSigningResponseBody signingResponse;
			using (var httpSigningResponse = await client.PostJsonAsync(csrUri, signingRequest))
				signingResponse = await httpSigningResponse.DeserializeAsync<CertificateSigningResponseBody>();
			if (signingResponse.Response == null)
				throw new InvalidDataException("CertificateSigningResponse does not contain a response");
			if (signingResponse.Response.X509Certificate == null)
				throw new InvalidDataException("CertificateSigningResponse does not contain a certificate");

			var signedCert = X509Certificate2.CreateFromPem(signingResponse.Response.X509Certificate);

			var confirmationRequest = new ConfirmationRequestBody
			{
				Request = new ConfirmationRequest
				{
					CertificateHash = Convert.ToBase64String(signedCert.GetCertHash(HashAlgorithmName.SHA256)),
					CertificateId = new CertificateId
					{
						Issuer = signedCert.Issuer,
						SerialNumber = int.Parse(signedCert.SerialNumber, System.Globalization.NumberStyles.HexNumber).ToString()
					},
					TransactionId = signingResponse.Response.TransactionId,
				}
			};

			using (var httpConfirmationResponse = await client.PostJsonAsync(ccrUri, confirmationRequest))
			{
				var confirmationResponse = await httpConfirmationResponse.DeserializeAsync<ConfirmationResponseBody>();
				if (confirmationResponse.Response == null)
					throw new InvalidDataException("Invalid ConfirmationResponse");

				if (confirmationResponse.Response.Status != CertificateSigningStatus.Accepted)
					throw new InvalidDataException("the CSR was not accepted");

				if (string.IsNullOrWhiteSpace(confirmationResponse.Response.Certificate))
					throw new InvalidDataException("no certificate was returned");
				//var caCert = X509Certificate2.CreateFromPem(confirmationResponse.Response.Certificate);
			}

			using (var pubPrivEphemeral = signedCert.CopyWithPrivateKey(privateKey))
			{
				await File.WriteAllTextAsync(
					certificateFilename,
					new String(PemEncoding.Write("PRIVATE KEY", privateKey.ExportPkcs8PrivateKey())) +
					"\r\n" +
					new String(PemEncoding.Write("CERTIFICATE", pubPrivEphemeral.Export(X509ContentType.Cert)))
				);
			}

			return signedCert;
		}

		// TODO: find a proper place for this
		public static async Task<Application<TPrimitiveContent>> RegisterAsync(Connection.IConnectionConfiguration m2mConfig, ApplicationConfiguration appConfig, Uri? caUri = null)
		{
			var con = new HttpConnection<TPrimitiveContent>(m2mConfig);

			var appId = appConfig.AppId ?? throw new ArgumentNullException("appConfig.AppId");
			var ae = await con.RegisterApplicationAsync(appConfig);
			if (ae == null)
				throw new InvalidOperationException("Unable to register application");

			if (con.ClientCertificate == null && caUri != null)
			{
				var certificateFilename = m2mConfig.CertificateFilename ?? throw new ArgumentNullException("m2mConfig.CertificateFilename");

				var signedCert = await GenerateSigningCertificateAsync(caUri, ae, certificateFilename);
				con = new HttpConnection<TPrimitiveContent>(m2mConfig.M2MUrl, signedCert);
			}

			return new Application<TPrimitiveContent>(con, ae, appConfig.CseId);
		}

	}

	public class Application : Application<PrimitiveContent>
	{

		public class Configuration : ApplicationConfiguration
		{
			public string? AEId { get; set; }
		}

		public Application(Connection<PrimitiveContent> con, AE ae, string urlPrefix) : base(con, ae, urlPrefix) {}
	}

}
