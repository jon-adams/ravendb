using System;
using System.Collections.Generic;
using System.Linq;
using Mono.CSharp;
using Raven.Abstractions.Data;
using Raven.Abstractions.Exceptions;
using Raven.Abstractions.Logging;
using Raven.Database.Indexing;
using Raven.Database.Util;

namespace Raven.Database.Tasks
{
	public class TouchReferenceDocumentIfChangedTask : Task
	{
		private static readonly ILog logger = LogManager.GetCurrentClassLogger();
		public IDictionary<string, Etag> ReferencesToCheck { get; set; }

		public override string ToString()
		{
			return string.Format("Index: {0}, References: {1}", Index, string.Join(", ", ReferencesToCheck.Keys));
		}


		public override bool SeparateTasksByIndex
		{
			get { return false; }
		}

		public override void Merge(Task task)
		{
			var t = (TouchReferenceDocumentIfChangedTask)task;

			foreach (var kvp in t.ReferencesToCheck)
			{
				Etag etag;
				if (ReferencesToCheck.TryGetValue(kvp.Key, out etag) == false)
				{
					ReferencesToCheck[kvp.Key] = kvp.Value;
				}
				else
				{
					ReferencesToCheck[kvp.Key] = etag.CompareTo(kvp.Value) < 0 ? etag : kvp.Value;
				}
			}
		}

		public override void Execute(WorkContext context)
		{
			if (logger.IsDebugEnabled)
			{
				logger.Debug("Going to touch the following documents (LoadDocument references, need to check for concurrent transactions): {0}",
					string.Join(", ", ReferencesToCheck));
			}

			using (context.Database.TransactionalStorage.DisableBatchNesting())
			{
				var docsToTouch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				context.TransactionalStorage.Batch(accessor =>
				{
					foreach (var kvp in ReferencesToCheck)
					{
						var doc = accessor.Documents.DocumentMetadataByKey(kvp.Key, null);

					    if (doc == null)
					    {
                            logger.Debug("Cannot touch {0}, non existant document", kvp.Key);
					        continue;
					    }
					    if (doc.Etag == kvp.Value)
					    {
					        logger.Debug("Don't need to touch {0}, etag {1} is the same as when we last saw it", kvp.Key, doc.Etag);
                            continue;
					    }


						docsToTouch.Add(kvp.Key);
					}
				});

				using (context.Database.DocumentLock.Lock())
				{
					context.TransactionalStorage.Batch(accessor =>
					{
						foreach (var doc in docsToTouch)
						{
							try
							{
								Etag preTouchEtag;
								Etag afterTouchEtag;
								accessor.Documents.TouchDocument(doc, out preTouchEtag, out afterTouchEtag);
								logger.Debug("Touching document: {0}, etag before touch: {1}, after touch {2}", doc, preTouchEtag, afterTouchEtag);
							}
							catch (ConcurrencyException)
							{
                                logger.Info("Concurrency exception when touching {0}", doc);
							}
							context.Database.CheckReferenceBecauseOfDocumentUpdate(doc, accessor);
						}
					});
				}

			}
		}

		public override Task Clone()
		{
			return new TouchReferenceDocumentIfChangedTask
			{
				Index = Index,
				ReferencesToCheck = new Dictionary<string, Etag>(ReferencesToCheck, StringComparer.OrdinalIgnoreCase)
			};
		}
	}
}
