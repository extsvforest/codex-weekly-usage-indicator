namespace WeeklyUsageIndicator;

// A fixed, versioned helper is passed as public program text. Requests, including credentials,
// travel exclusively over the inherited private stdin pipe. Nothing is installed in Linux.
internal static class WslCodexLinuxHelper
{
    internal const string Source = """
import os, sys, json, stat, pwd, hashlib, base64, re, shutil, subprocess, tempfile, selectors, time, signal, ctypes, threading, urllib.parse
MAX = 1048576
owned = None
class Fail(Exception):
    pass
def fail(code):
    raise Fail(code)
def safe_path(path):
    if not isinstance(path, str) or not path.startswith('/'):
        fail('unsupported')
    path = os.path.abspath(path)
    if path.startswith('/mnt/'): fail('unsupported')
    part = '/'
    for item in path.split('/')[1:]:
        part = os.path.join(part, item)
        try:
            if stat.S_ISLNK(os.lstat(part).st_mode): fail('linked-path')
        except FileNotFoundError: pass
    return path
def read_private(path):
    safe_path(path)
    try: fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
    except FileNotFoundError: fail('missing-auth')
    try:
        info = os.fstat(fd)
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid(): fail('unsafe-permissions')
        if info.st_mode & 0o077: fail('unsafe-permissions')
        if info.st_size <= 0 or info.st_size > MAX: fail('invalid-auth')
        with os.fdopen(fd, 'rb', closefd=False) as f: result = f.read(MAX + 1)
        if len(result) != info.st_size: fail('auth-changed')
        return result
    finally: os.close(fd)
def writers():
    count = 0
    for name in os.listdir('/proc'):
        if not name.isdigit() or int(name) == os.getpid(): continue
        path = '/proc/' + name
        try:
            if os.stat(path).st_uid != os.getuid(): continue
            with open(path + '/cmdline', 'rb') as f: raw = f.read(16384)
            args = [p.decode('utf-8', 'replace') for p in raw.split(b'\x00') if p]
            if not args: continue
            with open(path + '/comm', 'r', encoding='utf-8', errors='replace') as f: comm = f.read(64).strip().lower()
            try: binary = os.path.basename(os.readlink(path + '/exe')).lower()
            except PermissionError:
                # WSL interop processes can expose cmdline/comm while refusing exe readlink.
                # Their readable process name still identifies Codex or the node launcher.
                binary = comm
            first = os.path.basename(args[0].replace('\\', '/')).lower()
            codex = binary in ('codex', 'codex.exe') or first in ('codex', 'codex.exe') or comm in ('codex', 'codex.exe')
            if binary in ('node', 'nodejs') and len(args) > 1:
                script = args[1].replace('\\', '/')
                codex = codex or ('@openai/codex/' in script or os.path.basename(script) in ('codex', 'codex.js'))
            if codex: count += 1
        except (FileNotFoundError, ProcessLookupError): pass
        except PermissionError: fail('writers-unknown')
    return count
def require_stopped():
    if writers(): fail('writers-running')
def config_check(home):
    for path in ('/etc/codex/config.toml', '/etc/codex/requirements.toml'):
        if os.path.lexists(path): fail('managed-config')
    if any(os.environ.get(k) for k in ('CODEX_API_KEY', 'OPENAI_API_KEY', 'CHATGPT_ACCESS_TOKEN')): fail('store-mode')
    path = safe_path(os.path.join(home, 'config.toml'))
    if not os.path.exists(path): return
    with open(path, 'rb') as f: raw = f.read(MAX + 1)
    if len(raw) > MAX: fail('unsupported')
    for line in raw.decode('utf-8-sig').splitlines():
        line = line.strip()
        if not line or line.startswith('#'): continue
        if 'cli_auth_credentials_store' in line and not re.fullmatch(r'''cli_auth_credentials_store\s*=\s*(["'])file\1\s*(#.*)?''', line):
            fail('store-mode')
        if re.match(r'''^["']?(forced_chatgpt_workspace_id|forced_login_method)["']?\s*=''', line): fail('managed-config')
def native_cli(home):
    options = [shutil.which('codex')]
    options += [os.path.join(home, p) for p in ('.local/bin/codex', '.npm-global/bin/codex')]
    for option in options:
        if not option: continue
        path = os.path.realpath(option)
        if path.startswith('/mnt/') or path.lower().endswith('.exe') or not os.access(path, os.X_OK): continue
        with open(path, 'rb') as f: head = f.read(256)
        if not (head.startswith(b'\x7fELF') or head.startswith(b'#!')) or b'.exe' in head.split(b'\n')[0]: continue
        try:
            result = subprocess.run([path, '--version'], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                stderr=subprocess.DEVNULL, timeout=5, check=False)
            version = result.stdout[:128].decode('utf-8', 'replace').strip()
            if result.returncode == 0 and re.fullmatch(r'codex(?:-cli)?\s+[0-9][0-9A-Za-z.+-]*', version): return path, version
        except (OSError, subprocess.TimeoutExpired): pass
    fail('missing-cli')
def target_for(request):
    user = pwd.getpwuid(os.getuid())
    if user.pw_uid == 0: fail('unsupported')
    home = safe_path(user.pw_dir)
    configured = os.environ.get('CODEX_HOME') or os.path.join(home, '.codex')
    if not os.path.isabs(configured): fail('unsupported')
    codex_home = safe_path(configured)
    executable, version = native_cli(home)
    try: info = os.stat(codex_home)
    except FileNotFoundError: fail('missing-auth')
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != user.pw_uid or info.st_mode & 0o022: fail('unsafe-permissions')
    config_check(codex_home)
    target = dict(Distribution=request['distribution'], UserId=user.pw_uid, UserName=user.pw_name,
        HomeDirectory=home, CodexHome=codex_home, Executable=executable, Version=version)
    expected = request.get('target')
    if expected and any(expected.get(k) != target[k] for k in ('Distribution', 'UserId', 'UserName', 'HomeDirectory', 'CodexHome', 'Executable')):
        fail('environment-changed')
    return target
def stop_owned():
    global owned
    if owned is None: return
    try: os.killpg(owned.pid, signal.SIGTERM)
    except ProcessLookupError: pass
    try: owned.wait(timeout=3)
    except subprocess.TimeoutExpired:
        try: os.killpg(owned.pid, signal.SIGKILL)
        except ProcessLookupError: pass
        owned.wait(timeout=3)
    owned = None
def parent_gone(signum, frame):
    stop_owned()
    raise SystemExit(1)
def watch_parent():
    def eof():
        # BufferedReader holds a Python I/O lock during a blocking read. A daemon thread
        # holding that lock aborts normal interpreter shutdown; raw fd reads have no such lock.
        try: os.read(sys.stdin.fileno(), 1)
        finally: os.kill(os.getpid(), signal.SIGTERM)
    threading.Thread(target=eof, daemon=True).start()
def child_env(home):
    result = {k: v for k, v in os.environ.items() if not k.startswith(('CODEX_', 'OPENAI_', 'CHATGPT_', 'SSH_'))}
    result['CODEX_HOME'] = home
    result['RUST_LOG'] = 'off'
    return result
def verify(target):
    global owned
    require_stopped()
    data = json.loads(read_private(os.path.join(target['CodexHome'], 'auth.json')))
    try:
        payload = data['tokens']['id_token'].split('.')[1]
        claims = json.loads(base64.urlsafe_b64decode(payload + '=' * (-len(payload) % 4)))
        expected_email = claims.get('email')
    except Exception: fail('invalid-auth')
    try:
        owned = subprocess.Popen([target['Executable'], 'app-server', '--stdio'], stdin=subprocess.PIPE,
            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=child_env(target['CodexHome']),
            cwd=target['HomeDirectory'], start_new_session=True, bufsize=0)
        selector = selectors.DefaultSelector()
        selector.register(owned.stdout, selectors.EVENT_READ)
        buffered = bytearray()
        def send(value):
            owned.stdin.write((json.dumps(value, separators=(',', ':')) + '\n').encode())
            owned.stdin.flush()
        def response(wanted):
            deadline = time.monotonic() + 10
            total = 0
            while time.monotonic() < deadline:
                while b'\n' in buffered:
                    line, _, remaining = buffered.partition(b'\n')
                    buffered[:] = remaining
                    try: packet = json.loads(line)
                    except Exception: continue
                    if packet.get('id') == wanted:
                        if 'error' in packet: fail('verify-failed')
                        return packet.get('result', {})
                if not selector.select(max(0, deadline - time.monotonic())): break
                chunk = os.read(owned.stdout.fileno(), 4096)
                if not chunk: break
                total += len(chunk)
                if total > MAX: fail('verify-failed')
                buffered.extend(chunk)
            fail('verify-failed')
        send(dict(id=1, method='initialize', params=dict(clientInfo=dict(name='weekly-usage-indicator', version='1'))))
        response(1)
        send(dict(method='initialized'))
        send(dict(id=2, method='account/read', params=dict(refreshToken=False)))
        account = response(2).get('account') or {}
        valid = account.get('type') == 'chatgpt'
        if expected_email:
            valid = valid and isinstance(account.get('email'), str) and account['email'].casefold() == expected_email.casefold()
        return valid
    finally: stop_owned()
def login_progress(line):
    # Forward only the official CLI's authorization URL, never its surrounding diagnostics.
    # The Windows bridge validates it again and retains it only for the current login UI.
    if len(line) > 16384: return
    try:
        url = line.decode('utf-8').strip()
        if not url or any(c.isspace() or ord(c) < 32 or ord(c) == 127 or c == '\\' for c in url): return
        parsed = urllib.parse.urlsplit(url)
        if (parsed.scheme != 'https' or parsed.netloc.lower() not in ('auth.openai.com', 'auth.openai.com:443') or
            parsed.path != '/oauth/authorize' or not parsed.query or parsed.fragment): return
    except (ValueError, UnicodeError): return
    sys.stdout.write(json.dumps(dict(loginUrl=url), separators=(',', ':')) + '\n')
    sys.stdout.flush()
def wait_for_login(process):
    deadline = time.monotonic() + 570
    buffered = bytearray()
    total = 0
    with selectors.DefaultSelector() as selector:
        selector.register(process.stderr, selectors.EVENT_READ)
        while True:
            if time.monotonic() >= deadline: fail('login-failed')
            if not selector.select(min(1, max(0, deadline - time.monotonic()))): continue
            chunk = os.read(process.stderr.fileno(), 4096)
            if not chunk: break
            total += len(chunk)
            if total > MAX: fail('login-failed')
            buffered.extend(chunk)
            while b'\n' in buffered:
                line, _, remaining = buffered.partition(b'\n')
                buffered[:] = remaining
                login_progress(line)
        if buffered: login_progress(buffered)
    if process.wait(timeout=max(0.1, deadline - time.monotonic())) != 0: fail('login-failed')
def login(target):
    global owned
    # The official CLI owns OAuth and refresh; isolate its auth write from the active WSL account.
    # Existing CLI/daemon jobs may continue: this operation never reads or replaces their auth.
    root = safe_path(os.path.join(target['HomeDirectory'], '.codex-weekly-usage-logins'))
    os.makedirs(root, mode=0o700, exist_ok=True)
    info = os.stat(root)
    if info.st_uid != os.getuid() or info.st_mode & 0o077: fail('unsafe-permissions')
    stage = tempfile.mkdtemp(prefix='login-', dir=root)
    diagnostic_pipe = None
    try:
        with open(os.path.join(stage, 'config.toml'), 'x', encoding='utf-8') as f: f.write('cli_auth_credentials_store = "file"\n')
        owned = subprocess.Popen([target['Executable'], 'login', '-c', 'cli_auth_credentials_store="file"'],
            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
            env=child_env(stage), cwd=stage, start_new_session=True, bufsize=0)
        diagnostic_pipe = owned.stderr
        wait_for_login(owned)
        return read_private(os.path.join(stage, 'auth.json'))
    except subprocess.TimeoutExpired: fail('login-failed')
    finally:
        stop_owned()
        if diagnostic_pipe is not None: diagnostic_pipe.close()
        # The directory was created by us, contains only this CLI login, and is never a user's CODEX_HOME.
        safe_path(stage)
        shutil.rmtree(stage)
def main():
    raw = sys.stdin.buffer.readline(2 * MAX + 1)
    if len(raw) > 2 * MAX or not raw.endswith(b'\n'): fail('unsupported')
    request = json.loads(raw)
    watch_parent()
    target = target_for(request)
    action = request['action']
    path = os.path.join(target['CodexHome'], 'auth.json')
    if action == 'probe': return dict(target=target, writers=writers())
    if action == 'login': return dict(auth=base64.b64encode(login(target)).decode('ascii'))
    require_stopped()
    if action == 'read': return dict(auth=base64.b64encode(read_private(path)).decode('ascii'))
    if action == 'verify': return dict(verified=verify(target))
    if action != 'replace': fail('unsupported')
    before = read_private(path)
    if hashlib.sha256(before).hexdigest().upper() != request['expectedDigest']: fail('auth-changed')
    after = base64.b64decode(request['auth'], validate=True)
    if len(after) <= 0 or len(after) > MAX: fail('invalid-auth')
    json.loads(after)
    fd, temporary = tempfile.mkstemp(prefix='.gfs-wsl-auth-', dir=target['CodexHome'])
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, 'wb') as f:
            f.write(after); f.flush(); os.fsync(f.fileno())
        require_stopped()
        config_check(target['CodexHome'])
        if hashlib.sha256(read_private(path)).hexdigest().upper() != request['expectedDigest']: fail('auth-changed')
        safe_path(path)
        os.replace(temporary, path)
        directory = os.open(target['CodexHome'], os.O_DIRECTORY | os.O_RDONLY)
        try: os.fsync(directory)
        finally: os.close(directory)
    finally:
        if os.path.lexists(temporary): os.unlink(temporary)
    return {}
try:
    # If wsl.exe is closed or cancelled, only this helper's owned login/app-server group is terminated.
    signal.signal(signal.SIGTERM, parent_gone)
    if ctypes.CDLL(None).prctl(1, signal.SIGTERM, 0, 0, 0) != 0: fail('unsupported')
    output = main()
    output['ok'] = True
except Fail as e: output = dict(ok=False, code=str(e))
except Exception: output = dict(ok=False, code='helper-failed')
finally: stop_owned()
sys.stdout.write(json.dumps(output, separators=(',', ':')) + '\n')
""";
}
