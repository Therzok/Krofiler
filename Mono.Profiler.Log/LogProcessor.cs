// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Mono.Profiler.Log
{
	public sealed class LogProcessor
	{
		public Stream Stream { get; }

		public LogEventVisitor ImmediateVisitor { get; }

		public LogEventVisitor SortedVisitor { get; }

		public LogStreamHeader StreamHeader { get; private set; }

		LogReader _reader;

		LogBufferHeader _bufferHeader;

		ulong _time;

		bool _used;

		public LogProcessor (Stream stream, LogEventVisitor immediateVisitor, LogEventVisitor sortedVisitor)
		{
			if (stream == null)
				throw new ArgumentNullException (nameof (stream));

			Stream = stream;
			ImmediateVisitor = immediateVisitor;
			SortedVisitor = sortedVisitor;
		}

		public void Process ()
		{
			Process (CancellationToken.None);
		}

		static void ProcessEvent (LogEventVisitor visitor, LogEvent ev)
		{
			if (visitor != null) {
				visitor.VisitBefore (ev);
				ev.Accept (visitor);
				visitor.VisitAfter (ev);
			}
		}

		void ProcessEvents (List<LogEvent> events)
		{
			foreach (var ev in events.OrderBy (x => x.Timestamp)) {
				ProcessEvent (SortedVisitor, ev);
			}

			events.Clear ();
		}

		public void Process (CancellationToken token, bool live = false)
		{
			if (_used)
				throw new InvalidOperationException ("This log processor cannot be reused.");

			_used = true;
			_reader = new LogReader (Stream, true);

			StreamHeader = new LogStreamHeader (_reader);

			List<LogEvent> events = null;
			if (SortedVisitor != null)
				events = new List<LogEvent> (Environment.ProcessorCount * 1000);
			var memoryStream = new MemoryStream (4096 * 16);
			while (live || (Stream.Position < Stream.Length)) {
				token.ThrowIfCancellationRequested ();
				Wait (48, live, token);
				_bufferHeader = new LogBufferHeader (StreamHeader, _reader);

				Wait (_bufferHeader.Length, live, token);
				memoryStream.Position = 0;
				memoryStream.SetLength (_bufferHeader.Length);
				if (Stream.Read (memoryStream.GetBuffer (), 0, _bufferHeader.Length) != _bufferHeader.Length)
					throw new InvalidOperationException ();
				using (var reader = new LogReader (memoryStream, true)) {
					var oldReader = _reader;

					_reader = reader;

					while (memoryStream.Position < memoryStream.Length) {
						var ev = ReadEvent ();

						ProcessEvent (ImmediateVisitor, ev);

						if (SortedVisitor != null) {
							events.Add (ev);

							if (ev is SynchronizationPointEvent)
								ProcessEvents (events);
						}
					}

					_reader = oldReader;
				}
			}
			if (SortedVisitor != null)
				ProcessEvents (events);
		}

		private void Wait (int requestedBytes, bool live, CancellationToken cancelation)
		{
			while (Stream.Length - Stream.Position < requestedBytes) {
				if (live) {
					cancelation.ThrowIfCancellationRequested ();
					Thread.Sleep (100);
				} else
					throw new EndOfStreamException ();
			}
		}

		LogEvent ReadEvent ()
		{
			var type = _reader.ReadByte ();
			var basicType = (LogEventType)(type & 0xf);
			var extType = (LogEventType)(type & 0xf0);

			_time = ReadTime ();
			LogEvent ev = null;

			switch (basicType) {
				case LogEventType.Allocation:
					switch (extType) {
						case LogEventType.AllocationBacktrace:
						case LogEventType.AllocationNoBacktrace:
							ev = new AllocationEvent {
								ClassPointer = ReadPointer (),
								ObjectPointer = ReadObject (),
								ObjectSize = (long)_reader.ReadULeb128 (),
								Backtrace = ReadBacktrace (extType == LogEventType.AllocationBacktrace),
							};
							break;
						default:
							throw new LogException ($"Invalid extended event type ({extType}).");
					}
					break;
				case LogEventType.GC:
					switch (extType) {
						case LogEventType.GCEvent:
							ev = new GCEvent {
								Type = (LogGCEvent)_reader.ReadByte (),
								Generation = _reader.ReadByte (),
							};
							break;
						case LogEventType.GCResize:
							ev = new GCResizeEvent {
								NewSize = (long)_reader.ReadULeb128 (),
							};
							break;
						case LogEventType.GCMove: {
								var list = new long [(int)_reader.ReadULeb128 ()];

								for (var i = 0; i < list.Length; i++)
									list [i] = ReadObject ();

								ev = new GCMoveEvent {
									OldObjectPointers = list.Where ((_, i) => i % 2 == 0).ToArray (),
									NewObjectPointers = list.Where ((_, i) => i % 2 != 0).ToArray (),
								};
								break;
							}
						case LogEventType.GCHandleCreationNoBacktrace:
						case LogEventType.GCHandleCreationBacktrace:
							ev = new GCHandleCreationEvent {
								Type = (LogGCHandleType)_reader.ReadULeb128 (),
								Handle = (long)_reader.ReadULeb128 (),
								ObjectPointer = ReadObject (),
								Backtrace = ReadBacktrace (extType == LogEventType.GCHandleCreationBacktrace),
							};
							break;
						case LogEventType.GCHandleDeletionNoBacktrace:
						case LogEventType.GCHandleDeletionBacktrace:
							ev = new GCHandleDeletionEvent {
								Type = (LogGCHandleType)_reader.ReadULeb128 (),
								Handle = (long)_reader.ReadULeb128 (),
								Backtrace = ReadBacktrace (extType == LogEventType.GCHandleDeletionBacktrace),
							};
							break;
						case LogEventType.GCFinalizeBegin:
							ev = new GCFinalizeBeginEvent ();
							break;
						case LogEventType.GCFinalizeEnd:
							ev = new GCFinalizeEndEvent ();
							break;
						case LogEventType.GCFinalizeObjectBegin:
							ev = new GCFinalizeObjectBeginEvent {
								ObjectPointer = ReadObject (),
							};
							break;
						case LogEventType.GCFinalizeObjectEnd:
							ev = new GCFinalizeObjectEndEvent {
								ObjectPointer = ReadObject (),
							};
							break;
						default:
							throw new LogException ($"Invalid extended event type ({extType}).");
					}
					break;
				case LogEventType.Metadata: {
						var load = false;
						var unload = false;

						switch (extType) {
							case LogEventType.MetadataExtra:
								break;
							case LogEventType.MetadataEndLoad:
								load = true;
								break;
							case LogEventType.MetadataEndUnload:
								unload = true;
								break;
							default:
								throw new LogException ($"Invalid extended event type ({extType}).");
						}

						var metadataType = (LogMetadataType)_reader.ReadByte ();

						switch (metadataType) {
							case LogMetadataType.Class:
								if (load) {
									ev = new ClassLoadEvent {
										ClassPointer = ReadPointer (),
										ImagePointer = ReadPointer (),
										Name = _reader.ReadCString (),
									};
								} else
									throw new LogException ("Invalid class metadata event.");
								break;
							case LogMetadataType.Image:
								if (load) {
									ev = new ImageLoadEvent {
										ImagePointer = ReadPointer (),
										Name = _reader.ReadCString (),
									};
								} else if (unload) {
									ev = new ImageUnloadEvent {
										ImagePointer = ReadPointer (),
										Name = _reader.ReadCString (),
									};
								} else
									throw new LogException ("Invalid image metadata event.");
								break;
							case LogMetadataType.Assembly:
								if (load) {
									ev = new AssemblyLoadEvent {
										AssemblyPointer = ReadPointer (),
										ImagePointer = StreamHeader.FormatVersion >= 14 ? ReadPointer () : 0,
										Name = _reader.ReadCString (),
									};
								} else if (unload) {
									ev = new AssemblyUnloadEvent {
										AssemblyPointer = ReadPointer (),
										ImagePointer = StreamHeader.FormatVersion >= 14 ? ReadPointer () : 0,
										Name = _reader.ReadCString (),
									};
								} else
									throw new LogException ("Invalid assembly metadata event.");
								break;
							case LogMetadataType.AppDomain:
								if (load) {
									ev = new AppDomainLoadEvent {
										AppDomainId = ReadPointer (),
									};
								} else if (unload) {
									ev = new AppDomainUnloadEvent {
										AppDomainId = ReadPointer (),
									};
								} else {
									ev = new AppDomainNameEvent {
										AppDomainId = ReadPointer (),
										Name = _reader.ReadCString (),
									};
								}
								break;
							case LogMetadataType.Thread:
								if (load) {
									ev = new ThreadStartEvent {
										ThreadId = ReadPointer (),
									};
								} else if (unload) {
									ev = new ThreadEndEvent {
										ThreadId = ReadPointer (),
									};
								} else {
									ev = new ThreadNameEvent {
										ThreadId = ReadPointer (),
										Name = _reader.ReadCString (),
									};
								}
								break;
							case LogMetadataType.Context:
								if (load) {
									ev = new ContextLoadEvent {
										ContextId = ReadPointer (),
										AppDomainId = ReadPointer (),
									};
								} else if (unload) {
									ev = new ContextUnloadEvent {
										ContextId = ReadPointer (),
										AppDomainId = ReadPointer (),
									};
								} else
									throw new LogException ("Invalid context metadata event.");
								break;
							default:
								throw new LogException ($"Invalid metadata type ({metadataType}).");
						}
						break;
					}
				case LogEventType.Method:
					switch (extType) {
						case LogEventType.MethodLeave:
							ev = new LeaveEvent {
								MethodPointer = ReadMethod (),
							};
							break;
						case LogEventType.MethodEnter:
							ev = new EnterEvent {
								MethodPointer = ReadMethod (),
							};
							break;
						case LogEventType.MethodLeaveExceptional:
							ev = new ExceptionalLeaveEvent {
								MethodPointer = ReadMethod (),
							};
							break;
						case LogEventType.MethodJit:
							ev = new JitEvent {
								MethodPointer = ReadMethod (),
								CodePointer = ReadPointer (),
								CodeSize = (long)_reader.ReadULeb128 (),
								Name = _reader.ReadCString (),
							};
							break;
						default:
							throw new LogException ($"Invalid extended event type ({extType}).");
					}
					break;
				case LogEventType.Exception:
					switch (extType) {
						case LogEventType.ExceptionThrowNoBacktrace:
						case LogEventType.ExceptionThrowBacktrace:
							ev = new ThrowEvent {
								ObjectPointer = ReadObject (),
								Backtrace = ReadBacktrace (extType == LogEventType.ExceptionThrowBacktrace),
							};
							break;
						case LogEventType.ExceptionClause:
							ev = new ExceptionClauseEvent {
								Type = (LogExceptionClause)_reader.ReadByte (),
								Index = (long)_reader.ReadULeb128 (),
								MethodPointer = ReadMethod (),
								ObjectPointer = StreamHeader.FormatVersion >= 14 ? ReadObject () : 0,
							};
							break;
						default:
							throw new LogException ($"Invalid extended event type ({extType}).");
					}
					break;
				case LogEventType.Monitor:
					if (StreamHeader.FormatVersion < 14) {
						if (extType.HasFlag (LogEventType.MonitorBacktrace)) {
							extType = LogEventType.MonitorBacktrace;
						} else {
							extType = LogEventType.MonitorNoBacktrace;
						}
					}
					switch (extType) {
						case LogEventType.MonitorNoBacktrace:
						case LogEventType.MonitorBacktrace:
							ev = new MonitorEvent {
								Event = StreamHeader.FormatVersion >= 14 ?
													(LogMonitorEvent)_reader.ReadByte () :
													(LogMonitorEvent)((((byte)type & 0xf0) >> 4) & 0x3),
								ObjectPointer = ReadObject (),
								Backtrace = ReadBacktrace (extType == LogEventType.MonitorBacktrace),
							};
							break;
						default:
							throw new LogException ($"Invalid extended event type ({extType}).");
					}
					break;
				case LogEventType.Heap:
					switch (extType) {
						case LogEventType.HeapBegin:
							ev = new HeapBeginEvent ();
							break;
						case LogEventType.HeapEnd:
							ev = new HeapEndEvent ();
							break;
						case LogEventType.HeapObject: {
								HeapObjectEvent hoe = new HeapObjectEvent {
									ObjectPointer = ReadObject (),
									ClassPointer = ReadPointer (),
									ObjectSize = (long)_reader.ReadULeb128 (),
								};

								var list = new HeapObjectEvent.HeapObjectReference [(int)_reader.ReadULeb128 ()];

								for (var i = 0; i < list.Length; i++) {
									list [i] = new HeapObjectEvent.HeapObjectReference {
										Offset = (long)_reader.ReadULeb128 (),
										ObjectPointer = ReadObject (),
									};
								}

								hoe.References = list;
								ev = hoe;

								break;
							}

						case LogEventType.HeapRoots: {
								// TODO: This entire event makes no sense.
								var hre = new HeapRootsEvent ();
								var list = new HeapRootsEvent.HeapRoot [(int)_reader.ReadULeb128 ()];

								hre.MaxGenerationCollectionCount = (long)_reader.ReadULeb128 ();

								for (var i = 0; i < list.Length; i++) {
									list [i] = new HeapRootsEvent.HeapRoot {
										ObjectPointer = ReadObject (),
										Attributes = StreamHeader.FormatVersion == 13 ? (LogHeapRootAttributes)_reader.ReadByte () : (LogHeapRootAttributes)_reader.ReadULeb128 (),
										ExtraInfo = (long)_reader.ReadULeb128 (),
									};
								}

								hre.Roots = list;
								ev = hre;

								break;
							}
						default:
							throw new LogException ($"Invalid extended event type ({extType}).");
					}
					break;
				case LogEventType.Sample:
					switch (extType) {
						case LogEventType.SampleHit:
							if (StreamHeader.FormatVersion < 14) {
								// Read SampleType (always set to .Cycles) for versions < 14
								_reader.ReadByte ();
							}
							ev = new SampleHitEvent {
								ThreadId = ReadPointer (),
								UnmanagedBacktrace = ReadBacktrace (true, false),
								ManagedBacktrace = ReadBacktrace (true).Reverse ().ToArray (),
							};
							break;
						case LogEventType.SampleUnmanagedSymbol:
							ev = new UnmanagedSymbolEvent {
								CodePointer = ReadPointer (),
								CodeSize = (long)_reader.ReadULeb128 (),
								Name = _reader.ReadCString (),
							};
							break;
						case LogEventType.SampleUnmanagedBinary:
							ev = new UnmanagedBinaryEvent {
								SegmentPointer = StreamHeader.FormatVersion >= 14 ? ReadPointer () : _reader.ReadSLeb128 (),
								SegmentOffset = (long)_reader.ReadULeb128 (),
								SegmentSize = (long)_reader.ReadULeb128 (),
								FileName = _reader.ReadCString (),
							};
							break;
						case LogEventType.SampleCounterDescriptions: {
								var cde = new CounterDescriptionsEvent ();
								var list = new CounterDescriptionsEvent.CounterDescription [(int)_reader.ReadULeb128 ()];

								for (var i = 0; i < list.Length; i++) {
									var section = (LogCounterSection)_reader.ReadULeb128 ();

									list [i] = new CounterDescriptionsEvent.CounterDescription {
										Section = section,
										SectionName = section == LogCounterSection.User ? _reader.ReadCString () : null,
										CounterName = _reader.ReadCString (),
										Type = (LogCounterType)_reader.ReadByte (),
										Unit = (LogCounterUnit)_reader.ReadByte (),
										Variance = (LogCounterVariance)_reader.ReadByte (),
										Index = (long)_reader.ReadULeb128 (),
									};
								}

								cde.Descriptions = list;
								ev = cde;

								break;
							}
						case LogEventType.SampleCounters: {
								var cse = new CounterSamplesEvent ();
								var list = new List<CounterSamplesEvent.CounterSample> ();

								while (true) {
									var index = (long)_reader.ReadULeb128 ();

									if (index == 0)
										break;

									var counterType = (LogCounterType)_reader.ReadByte ();

									object value = null;

									switch (counterType) {
										case LogCounterType.String:
											value = _reader.ReadByte () == 1 ? _reader.ReadCString () : null;
											break;
										case LogCounterType.Int32:
										case LogCounterType.Word:
										case LogCounterType.Int64:
										case LogCounterType.Interval:
											value = _reader.ReadSLeb128 ();
											break;
										case LogCounterType.UInt32:
										case LogCounterType.UInt64:
											value = _reader.ReadULeb128 ();
											break;
										case LogCounterType.Double:
											value = _reader.ReadDouble ();
											break;
										default:
											throw new LogException ($"Invalid counter type ({counterType}).");
									}

									list.Add (new CounterSamplesEvent.CounterSample {
										Index = index,
										Type = counterType,
										Value = value,
									});
								}

								cse.Samples = list;
								ev = cse;

								break;
							}
						default:
							throw new LogException ($"Invalid extended event type ({extType}).");
					}
					break;
				case LogEventType.Runtime:
					switch (extType) {
						case LogEventType.RuntimeJitHelper: {
								var helperType = (LogJitHelper)_reader.ReadByte ();

								ev = new JitHelperEvent {
									Type = helperType,
									BufferPointer = ReadPointer (),
									BufferSize = (long)_reader.ReadULeb128 (),
									Name = helperType == LogJitHelper.SpecificTrampoline ? _reader.ReadCString () : null,
								};
								break;
							}
						default:
							throw new LogException ($"Invalid extended event type ({extType}).");
					}
					break;
				case LogEventType.Meta:
					switch (extType) {
						case LogEventType.MetaSynchronizationPoint:
							ev = new SynchronizationPointEvent {
								Type = (LogSynchronizationPoint)_reader.ReadByte (),
							};
							break;
						default:
							throw new LogException ($"Invalid extended event type ({extType}).");
					}
					break;
				default:
					throw new LogException ($"Invalid basic event type ({basicType}).");
			}

			ev.Timestamp = _time;
			ev.Buffer = _bufferHeader;

			return ev;
		}

		long ReadPointer ()
		{
			var ptr = _reader.ReadSLeb128 () + _bufferHeader.PointerBase;

			return StreamHeader.PointerSize == sizeof (long) ? ptr : ptr & 0xffffffffL;
		}

		long ReadObject ()
		{
			return _reader.ReadSLeb128 () + _bufferHeader.ObjectBase << 3;
		}

		long ReadMethod ()
		{
			return _bufferHeader.CurrentMethod += _reader.ReadSLeb128 ();
		}

		ulong ReadTime ()
		{
			return _bufferHeader.CurrentTime += _reader.ReadULeb128 ();
		}

		IReadOnlyList<long> ReadBacktrace (bool actuallyRead, bool managed = true)
		{
			if (!actuallyRead)
				return Array.Empty<long> ();

			var list = new long [(int)_reader.ReadULeb128 ()];

			for (var i = 0; i < list.Length; i++)
				list [i] = managed ? ReadMethod () : ReadPointer ();

			return list;
		}
	}
}
