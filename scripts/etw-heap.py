# Read-only: the managed heap of one .NET process by type, and what holds its large objects, from an ETW heap walk
# of the .NET runtime (GCBulkNode/GCBulkEdge/BulkType/GCBulkRootStaticVar). The events carry object addresses, sizes,
# types and references, never what the objects contain. Nothing to install: logman and tracing come with Windows, and
# a member of Performance Log Users may start the session.
#
#   logman start widget-heap -p "{e13c0d23-ccbc-4e12-931b-d9cc2eee27e4}" 0x1980001 5 -bs 1024 -nb 128 640 -o heap.etl -ets
#   (wait until heap.etl stops growing: the walk forces one full collection in every .NET process on the PC)
#   logman stop widget-heap -ets
#   python scripts/etw-heap.py heap.etl <pid> [type of the holders to follow]
#
# Keywords: GC | GCHeapCollect | GCHeapDump | Type | GCHeapAndTypeNames. For collections and heap sizes alone
# (GCHeapStats, no walk) use 0x800001 instead. tracerpt drops the bulk events' arrays, hence this reader.
import ctypes, struct, sys, collections
from ctypes import wintypes

etl, pid = sys.argv[1], int(sys.argv[2])

class GUID(ctypes.Structure):
    _fields_ = [('Data1', ctypes.c_uint32), ('Data2', ctypes.c_uint16), ('Data3', ctypes.c_uint16), ('Data4', ctypes.c_ubyte * 8)]
class EVENT_DESCRIPTOR(ctypes.Structure):
    _fields_ = [('Id', ctypes.c_uint16), ('Version', ctypes.c_ubyte), ('Channel', ctypes.c_ubyte), ('Level', ctypes.c_ubyte),
                ('Opcode', ctypes.c_ubyte), ('Task', ctypes.c_uint16), ('Keyword', ctypes.c_uint64)]
class EVENT_HEADER(ctypes.Structure):
    _fields_ = [('Size', ctypes.c_uint16), ('HeaderType', ctypes.c_uint16), ('Flags', ctypes.c_uint16), ('EventProperty', ctypes.c_uint16),
                ('ThreadId', ctypes.c_uint32), ('ProcessId', ctypes.c_uint32), ('TimeStamp', ctypes.c_int64), ('ProviderId', GUID),
                ('EventDescriptor', EVENT_DESCRIPTOR), ('ProcessorTime', ctypes.c_uint64), ('ActivityId', GUID)]
class ETW_BUFFER_CONTEXT(ctypes.Structure):
    _fields_ = [('ProcessorNumber', ctypes.c_ubyte), ('Alignment', ctypes.c_ubyte), ('LoggerId', ctypes.c_uint16)]
class EVENT_RECORD(ctypes.Structure):
    _fields_ = [('EventHeader', EVENT_HEADER), ('BufferContext', ETW_BUFFER_CONTEXT), ('ExtendedDataCount', ctypes.c_uint16),
                ('UserDataLength', ctypes.c_uint16), ('ExtendedData', ctypes.c_void_p), ('UserData', ctypes.c_void_p),
                ('UserContext', ctypes.c_void_p)]
class EVENT_TRACE_HEADER(ctypes.Structure):
    _fields_ = [('Size', ctypes.c_uint16), ('FieldTypeFlags', ctypes.c_uint16), ('Version', ctypes.c_uint32), ('ThreadId', ctypes.c_uint32),
                ('ProcessId', ctypes.c_uint32), ('TimeStamp', ctypes.c_int64), ('Guid', GUID), ('ProcessorTime', ctypes.c_uint64)]
class EVENT_TRACE(ctypes.Structure):
    _fields_ = [('Header', EVENT_TRACE_HEADER), ('InstanceId', ctypes.c_uint32), ('ParentInstanceId', ctypes.c_uint32),
                ('ParentGuid', GUID), ('MofData', ctypes.c_void_p), ('MofLength', ctypes.c_uint32), ('ClientContext', ctypes.c_uint32)]
class SYSTEMTIME(ctypes.Structure):
    _fields_ = [(name, ctypes.c_uint16) for name in ('wYear', 'wMonth', 'wDayOfWeek', 'wDay', 'wHour', 'wMinute', 'wSecond', 'wMilliseconds')]
class TIME_ZONE_INFORMATION(ctypes.Structure):
    _fields_ = [('Bias', ctypes.c_int32), ('StandardName', ctypes.c_wchar * 32), ('StandardDate', SYSTEMTIME), ('StandardBias', ctypes.c_int32),
                ('DaylightName', ctypes.c_wchar * 32), ('DaylightDate', SYSTEMTIME), ('DaylightBias', ctypes.c_int32)]
class TRACE_LOGFILE_HEADER(ctypes.Structure):
    _fields_ = [('BufferSize', ctypes.c_uint32), ('Version', ctypes.c_uint32), ('ProviderVersion', ctypes.c_uint32),
                ('NumberOfProcessors', ctypes.c_uint32), ('EndTime', ctypes.c_int64), ('TimerResolution', ctypes.c_uint32),
                ('MaximumFileSize', ctypes.c_uint32), ('LogFileMode', ctypes.c_uint32), ('BuffersWritten', ctypes.c_uint32),
                ('LogInstanceGuid', GUID), ('LoggerName', ctypes.c_void_p), ('LogFileName', ctypes.c_void_p),
                ('TimeZone', TIME_ZONE_INFORMATION), ('BootTime', ctypes.c_int64), ('PerfFreq', ctypes.c_int64), ('StartTime', ctypes.c_int64),
                ('ReservedFlags', ctypes.c_uint32), ('BuffersLost', ctypes.c_uint32)]
CALLBACK = ctypes.WINFUNCTYPE(None, ctypes.POINTER(EVENT_RECORD))
class EVENT_TRACE_LOGFILEW(ctypes.Structure):
    _fields_ = [('LogFileName', ctypes.c_wchar_p), ('LoggerName', ctypes.c_wchar_p), ('CurrentTime', ctypes.c_int64),
                ('BuffersRead', ctypes.c_uint32), ('ProcessTraceMode', ctypes.c_uint32), ('CurrentEvent', EVENT_TRACE),
                ('LogfileHeader', TRACE_LOGFILE_HEADER), ('BufferCallback', ctypes.c_void_p), ('BufferSize', ctypes.c_uint32),
                ('Filled', ctypes.c_uint32), ('EventsLost', ctypes.c_uint32), ('EventRecordCallback', CALLBACK),
                ('IsKernelTrace', ctypes.c_uint32), ('Context', ctypes.c_void_p)]

CLR = (0xe13c0d23, 0xccbc, 0x4e12, bytes([0x93, 0x1b, 0xd9, 0xcc, 0x2e, 0xee, 0x27, 0xe4]))
payloads = collections.defaultdict(list)
def on_event(record):
    header = record.contents.EventHeader
    if header.ProcessId != pid: return
    provider = header.ProviderId
    if (provider.Data1, provider.Data2, provider.Data3, bytes(provider.Data4)) != CLR: return
    event_id = header.EventDescriptor.Id
    if event_id in (15, 16, 18, 19, 38, 4):
        payloads[event_id].append(ctypes.string_at(record.contents.UserData, record.contents.UserDataLength))
callback = CALLBACK(on_event)

advapi = ctypes.WinDLL('advapi32')
advapi.OpenTraceW.restype = ctypes.c_uint64
advapi.OpenTraceW.argtypes = [ctypes.POINTER(EVENT_TRACE_LOGFILEW)]
advapi.ProcessTrace.argtypes = [ctypes.POINTER(ctypes.c_uint64), ctypes.c_uint32, ctypes.c_void_p, ctypes.c_void_p]
advapi.CloseTrace.argtypes = [ctypes.c_uint64]
log = EVENT_TRACE_LOGFILEW()
log.LogFileName = etl
log.ProcessTraceMode = 0x10000000  # PROCESS_TRACE_MODE_EVENT_RECORD
log.EventRecordCallback = callback
handle = ctypes.c_uint64(advapi.OpenTraceW(ctypes.byref(log)))
if handle.value == 0xFFFFFFFFFFFFFFFF: sys.exit('OpenTrace failed: %d' % ctypes.GetLastError())
status = advapi.ProcessTrace(ctypes.byref(handle), 1, None, None)
advapi.CloseTrace(handle)
print('ProcessTrace', status, {k: len(v) for k, v in payloads.items()})

def utf16z(data, offset):
    end = offset
    while data[end:end + 2] != b'\0\0': end += 2
    return data[offset:end].decode('utf-16-le', 'replace'), end + 2

types = {}
for data in payloads[15]:
    count = struct.unpack_from('<I', data, 0)[0]; offset = 6
    for _ in range(count):
        type_id, module, name_id, flags, element = struct.unpack_from('<QQIIB', data, offset); offset += 25
        name, offset = utf16z(data, offset)
        n = struct.unpack_from('<I', data, offset)[0]; offset += 4
        arguments = struct.unpack_from('<%dQ' % n, data, offset); offset += 8 * n
        types[type_id] = (name, arguments)
def type_name(type_id, depth=0):
    name, arguments = types.get(type_id, ('?%x' % type_id, ()))
    if arguments and depth < 3: name += '<' + ','.join(type_name(a, depth + 1) for a in arguments) + '>'
    return name

nodes = []   # (address, size, type, edge count)
for data in sorted(payloads[18], key=lambda d: struct.unpack_from('<I', d, 0)[0]):
    index, count = struct.unpack_from('<II', data, 0)
    nodes.extend(struct.iter_unpack('<QQQQ', data[10:10 + 32 * count]))
edges = []
for data in sorted(payloads[19], key=lambda d: struct.unpack_from('<I', d, 0)[0]):
    index, count = struct.unpack_from('<II', data, 0)
    edges.extend(value for value, field in struct.iter_unpack('<QI', data[10:10 + 12 * count]))
statics = {}
for data in payloads[38]:
    count = struct.unpack_from('<I', data, 0)[0]; offset = 14
    for _ in range(count):
        root, object_id, type_id, flags = struct.unpack_from('<QQQI', data, offset); offset += 28
        field, offset = utf16z(data, offset)
        statics[object_id] = '%s.%s' % (type_name(type_id), field)
for data in payloads[4]:
    sizes = struct.unpack_from('<10Q', data, 0)
    print('heap stats: gen0 %.0f gen1 %.0f gen2 %.0f (live %.0f) loh %.0f (live %.0f) MB' % (
        sizes[0] / 2**20, sizes[2] / 2**20, sizes[4] / 2**20, sizes[5] / 2**20, sizes[6] / 2**20, sizes[7] / 2**20))

MB = 2 ** 20
print('types %d, objects %d (%.1f MB), edges %d (expected %d), statics %d' % (
    len(types), len(nodes), sum(n[1] for n in nodes) / MB, len(edges), sum(n[3] for n in nodes), len(statics)))
by_type = collections.defaultdict(lambda: [0, 0])
for address, size, type_id, edge_count in nodes:
    by_type[type_id][0] += size; by_type[type_id][1] += 1
print('\nby type:')
for type_id, (size, count) in sorted(by_type.items(), key=lambda item: -item[1][0])[:25]:
    print('  %8.1f MB %9d  %s' % (size / MB, count, type_name(type_id)))

starts = []
position = 0
for node in nodes:
    starts.append(position); position += node[3]
by_address = {node[0]: i for i, node in enumerate(nodes)}

def parents_of(targets):
    found = {}
    for i, (address, size, type_id, edge_count) in enumerate(nodes):
        if edge_count:
            span = edges[starts[i]:starts[i] + edge_count]
            if not targets.isdisjoint(span):
                for target in span:
                    if target in targets and target not in found: found[target] = i
    return found

large = {node[0] for node in nodes if node[1] >= 256 * 1024}
print('\nobjects of 256 KB or more: %d, %.1f MB' % (len(large), sum(nodes[by_address[a]][1] for a in large) / MB))
holders = parents_of(large)
groups = collections.defaultdict(lambda: [0, 0, set()])
for target in large:
    node = nodes[by_address[target]]
    holder = holders.get(target)
    key = (type_name(node[2]), type_name(nodes[holder][2]) if holder is not None else '(root/static)')
    groups[key][0] += node[1]; groups[key][1] += 1
    if holder is not None: groups[key][2].add(nodes[holder][0])
for (child, parent), (size, count, _) in sorted(groups.items(), key=lambda item: -item[1][0])[:15]:
    print('  %8.1f MB %5d  %s  <-  %s' % (size / MB, count, child, parent))

# Chains from the biggest holders up to a static field or a root.
weight = collections.Counter()
for target in large:
    holder = holders.get(target)
    if holder is not None: weight[nodes[holder][0]] += nodes[by_address[target]][1]
only = sys.argv[3] if len(sys.argv) > 3 else None
top = [address for address, _ in weight.most_common()
       if only is None or only in type_name(nodes[by_address[address]][2])][:6]
chains = {address: [type_name(nodes[by_address[address]][2])] for address in top}
frontier = {address: address for address in top}
for level in range(14):
    if not frontier: break
    ups = parents_of(set(frontier))
    following = {}
    for target, origin in frontier.items():
        if target in statics: chains[origin].append('static ' + statics[target]); continue
        up = ups.get(target)
        if up is None: chains[origin].append('(root)'); continue
        chains[origin].append(type_name(nodes[up][2]))
        following.setdefault(nodes[up][0], origin)
    frontier = following
print('\nwhat holds the biggest holders:')
for address in top:
    print('  %.1f MB: %s' % (weight[address] / MB, '  <-  '.join(chains[address])))
