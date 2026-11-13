#!/bin/sh
# Rebuild V380Decoder from main and recreate every container using the reference
# container's exact Docker image ID. Save original containers until success.
set -eu

SOURCE_DIR=${V380_DIR:-/opt/v380}
REFERENCE=${V380_REFERENCE:-v380_58780462}
IMAGE_TAG=${V380_IMAGE:-v380decoder:latest}
CHECK_SECONDS=${V380_CHECK_SECONDS:-5}

command -v docker >/dev/null 2>&1 || { echo 'ERROR: docker is not installed' >&2; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo 'ERROR: python3 is required' >&2; exit 1; }
command -v git >/dev/null 2>&1 || { echo 'ERROR: git is required' >&2; exit 1; }

docker info >/dev/null 2>&1 || { echo 'ERROR: cannot connect to Docker' >&2; exit 1; }
[ -d "$SOURCE_DIR" ] || { echo "ERROR: source directory not found: $SOURCE_DIR" >&2; exit 1; }

OLD_IMAGE_ID=$(docker inspect --type=container --format '{{.Image}}' "$REFERENCE") || {
    echo "ERROR: reference container not found: $REFERENCE" >&2
    exit 1
}

case "$CHECK_SECONDS" in
    ''|*[!0-9]*) echo 'ERROR: V380_CHECK_SECONDS must be an integer' >&2; exit 1 ;;
esac

printf 'Reference container: %s\nOld image ID: %s\nSource directory: %s\n' \
    "$REFERENCE" "$OLD_IMAGE_ID" "$SOURCE_DIR"

cd "$SOURCE_DIR"
ORIGIN=$(git remote get-url origin) || { echo 'ERROR: no git origin remote' >&2; exit 1; }
case "$ORIGIN" in
    *AspirantDrago/V380Decoder* ) ;;
    * ) echo "ERROR: unexpected Git origin: $ORIGIN" >&2; exit 1 ;;
esac

BRANCH=$(git branch --show-current)
if [ "$BRANCH" != main ]; then
    echo "ERROR: expected main branch, current branch is: $BRANCH" >&2
    exit 1
fi

printf '\nUpdating main...\n'
git pull --ff-only origin main
printf '\nBuilding %s...\n' "$IMAGE_TAG"
docker build -t "$IMAGE_TAG" .
NEW_IMAGE_ID=$(docker image inspect --format '{{.Id}}' "$IMAGE_TAG")
if [ "$OLD_IMAGE_ID" = "$NEW_IMAGE_ID" ]; then
    echo 'Image is unchanged; there is nothing to recreate.'
    exit 0
fi

# All read/rename/create/start/remove operations below are performed via the
# local Docker Engine API so that Config and HostConfig are preserved verbatim.
# Python standard library only: no docker-py or jq installation required.
python3 - "$OLD_IMAGE_ID" "$IMAGE_TAG" "$CHECK_SECONDS" <<'PY'
import copy
import http.client
import json
import os
import secrets
import socket
import subprocess
import sys
import time
from urllib.parse import quote

old_image_id, new_image_tag, check_seconds_arg = sys.argv[1:]
check_seconds = int(check_seconds_arg)


def run(*args):
    return subprocess.check_output(args, text=True).strip()


def docker_socket():
    host = os.environ.get('DOCKER_HOST')
    if not host:
        try:
            host = run('docker', 'context', 'inspect', '--format', '{{.Endpoints.docker.Host}}')
        except (subprocess.CalledProcessError, OSError):
            host = 'unix:///var/run/docker.sock'
    if host.startswith('unix://'):
        path = host[len('unix://'):]
        if os.path.exists(path):
            return path
    raise RuntimeError(
        'This script requires a local Unix-socket Docker context; '
        'remote TCP/SSH contexts are not supported.'
    )


class UnixConnection(http.client.HTTPConnection):
    def __init__(self, path):
        super().__init__('localhost', timeout=90)
        self.path = path

    def connect(self):
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(self.path)


class DockerAPI:
    def __init__(self, path):
        self.path = path
        version = self.call('GET', '/version', versioned=False)['ApiVersion']
        self.prefix = '/v' + version

    def call(self, method, path, body=None, versioned=True, ok=(200, 201, 204, 304)):
        conn = UnixConnection(self.path)
        data = json.dumps(body).encode('utf-8') if body is not None else None
        headers = {'Content-Type': 'application/json'} if body is not None else {}
        url = (self.prefix if versioned else '') + path
        try:
            conn.request(method, url, body=data, headers=headers)
            response = conn.getresponse()
            raw = response.read()
            if response.status not in ok:
                try:
                    message = json.loads(raw.decode('utf-8')).get('message', '')
                except (UnicodeDecodeError, ValueError):
                    message = raw.decode('utf-8', errors='replace')
                raise RuntimeError(f'Docker {method} {path}: HTTP {response.status}: {message}')
            return json.loads(raw.decode('utf-8')) if raw else None
        finally:
            conn.close()

    def inspect(self, container_id):
        return self.call('GET', '/containers/' + quote(container_id, safe='') + '/json')

    def stop(self, container_id, timeout=15):
        self.call('POST', '/containers/' + quote(container_id, safe='') + f'/stop?t={timeout}')

    def rename(self, container_id, name):
        self.call('POST', '/containers/' + quote(container_id, safe='') + '/rename?name=' + quote(name, safe=''))

    def create(self, name, config):
        return self.call('POST', '/containers/create?name=' + quote(name, safe=''), config)['Id']

    def start(self, container_id):
        self.call('POST', '/containers/' + quote(container_id, safe='') + '/start')

    def remove(self, container_id, force=False):
        self.call('DELETE', '/containers/' + quote(container_id, safe='')
                  + '?v=0&force=' + ('1' if force else '0'))

    def network_connect(self, network, container_id, endpoint):
        self.call('POST', '/networks/' + quote(network, safe='') + '/connect',
                  {'Container': container_id, 'EndpointConfig': endpoint})


def endpoint_config(settings):
    # Inspect contains runtime-only IP, gateway, endpoint and network IDs.
    # Do not force dynamically assigned addresses into a replacement.
    result = {}
    for key in ('Aliases', 'Links', 'DriverOpts'):
        if settings.get(key):
            result[key] = settings[key]
    return result


def preserve_mounts(original, host_config):
    # Docker's HostConfig keeps explicitly configured mounts, but anonymous
    # volumes are recorded only in the container's Mounts[]. Rebind those
    # existing volume names instead of silently creating empty replacements.
    binds = list(host_config.get('Binds') or [])
    explicit = list(host_config.get('Mounts') or [])
    targets = {x.get('Target') for x in explicit}
    for spec in binds:
        parts = spec.split(':', 2)
        if len(parts) >= 2:
            targets.add(parts[1])
    for mount in original.get('Mounts') or []:
        target = mount.get('Destination')
        if not target or target in targets:
            continue
        kind = mount.get('Type')
        if kind == 'volume':
            source = mount.get('Name')
        elif kind == 'bind':
            source = mount.get('Source')
        elif kind == 'tmpfs':
            # Already part of HostConfig.Tmpfs; no user data to restore.
            if target not in (host_config.get('Tmpfs') or {}):
                raise RuntimeError(f'Cannot safely preserve tmpfs mount {target}')
            continue
        else:
            raise RuntimeError(f'Unsupported mount type {kind!r} at {target}')
        if not source:
            raise RuntimeError(f'No source for mount {target}')
        mode = 'rw' if mount.get('RW', True) else 'ro'
        binds.append(f'{source}:{target}:{mode}')
        targets.add(target)
    host_config['Binds'] = binds or None


def build_config(original):
    config = copy.deepcopy(original['Config'])
    host = copy.deepcopy(original['HostConfig'])
    config['Image'] = new_image_tag
    mode = host.get('NetworkMode') or 'default'
    networks = original.get('NetworkSettings', {}).get('Networks') or {}

    preserve_mounts(original, host)

    primary = mode if mode in networks else None
    extra_networks = []
    endpoint_map = {}
    if mode not in ('host', 'none'):
        if primary and primary not in ('bridge', 'default'):
            endpoint_map[primary] = endpoint_config(networks[primary])
        for network, options in networks.items():
            if network != primary and network not in ('host', 'none', 'bridge'):
                extra_networks.append((network, endpoint_config(options)))

    config['HostConfig'] = host
    if endpoint_map:
        config['NetworkingConfig'] = {'EndpointsConfig': endpoint_map}
    return config, extra_networks


def check_original(original):
    name = original['Name'].lstrip('/')
    labels = original.get('Config', {}).get('Labels') or {}
    if 'com.docker.compose.project' in labels:
        raise RuntimeError(f'{name}: managed by Docker Compose. Use docker compose up, not this script.')
    host = original['HostConfig']
    mode = host.get('NetworkMode') or ''
    if mode.startswith('container:'):
        raise RuntimeError(f'{name}: container-sharing network mode is not supported safely')
    if host.get('PublishAllPorts'):
        raise RuntimeError(f'{name}: dynamic -P published ports cannot be preserved reliably')
    if host.get('AutoRemove'):
        raise RuntimeError(f'{name}: --rm containers cannot be safely backed up')
    if host.get('Links'):
        raise RuntimeError(f'{name}: legacy --link references require manual recreation')
    for network, details in (original.get('NetworkSettings', {}).get('Networks') or {}).items():
        static = details.get('IPAMConfig') or {}
        if static.get('IPv4Address') or static.get('IPv6Address'):
            raise RuntimeError(f'{name}: static IP on {network}; refusing to risk an IP conflict')
    build_config(original)  # Validate mounts before stopping anything.


def wait_running(api, container_id, name, original):
    # Started is not sufficient if the process immediately exits.
    if not original.get('State', {}).get('Running'):
        return
    time.sleep(check_seconds)
    updated = api.inspect(container_id)
    state = updated.get('State') or {}
    if not state.get('Running') or state.get('Restarting'):
        raise RuntimeError(f'{name}: new container is not steadily running')
    health = (state.get('Health') or {}).get('Status')
    if health == 'unhealthy':
        raise RuntimeError(f'{name}: new container reports unhealthy')


def main():
    api = DockerAPI(docker_socket())
    ids = run('docker', 'ps', '-aq', '--no-trunc').splitlines()
    if not ids:
        raise RuntimeError('No containers found')
    all_containers = [api.inspect(container_id) for container_id in ids]
    originals = [c for c in all_containers if c.get('Image') == old_image_id]
    if not originals:
        raise RuntimeError(f'No containers are using the old image ID {old_image_id}')

    # All-or-nothing preflight. Fail before stopping anything incompatible.
    for original in originals:
        check_original(original)
    print(f'\nFound {len(originals)} container(s) to recreate:', flush=True)
    for original in originals:
        state = 'running' if original['State']['Running'] else 'stopped'
        print(f'  {original["Name"].lstrip("/")} ({state})', flush=True)

    changed = []
    try:
        for original in originals:
            ident = original['Id']
            name = original['Name'].lstrip('/')
            was_running = bool(original['State']['Running'])
            backup_name = name + '-v380backup-' + secrets.token_hex(4)
            item = {'id': ident, 'name': name, 'backup': backup_name,
                    'was_running': was_running, 'renamed': False, 'created': None}
            print(f'\n[{name}] Stopping and keeping backup...', flush=True)
            # Track it before stopping: failure between stop and rename must
            # also restore a running original container.
            changed.append(item)
            if was_running:
                timeout = original.get('Config', {}).get('StopTimeout') or 15
                api.stop(ident, timeout)
            api.rename(ident, backup_name)
            item['renamed'] = True

            config, extra_networks = build_config(original)
            new_id = api.create(name, config)
            item['created'] = new_id
            for net, endpoint in extra_networks:
                api.network_connect(net, new_id, endpoint)
            if was_running:
                api.start(new_id)
            wait_running(api, new_id, name, original)
            status = 'running' if was_running else 'stopped (as before)'
            print(f'[{name}] Recreated: {new_id[:12]} ({status})', flush=True)

    except Exception as exc:
        print(f'\nERROR: {exc}', file=sys.stderr, flush=True)
        print('Rolling back all changed containers...', file=sys.stderr, flush=True)
        failed = False
        for item in reversed(changed):
            try:
                if item['created']:
                    api.remove(item['created'], force=True)
                if item['renamed']:
                    api.rename(item['id'], item['name'])
                if item['was_running'] and not api.inspect(item['id'])['State']['Running']:
                    api.start(item['id'])
                print(f'  Restored {item["name"]}', file=sys.stderr, flush=True)
            except Exception as rollback_exc:
                failed = True
                print(f'  ROLLBACK FAILED for {item["name"]}: {rollback_exc}',
                      file=sys.stderr, flush=True)
        if failed:
            print('Backup containers might remain: inspect docker ps -a before cleanup.',
                  file=sys.stderr, flush=True)
        return 1

    print('\nAll new containers started successfully. Removing stopped backups...', flush=True)
    for item in changed:
        try:
            api.remove(item['id'], force=False)
            print(f'  Removed backup: {item["backup"]}', flush=True)
        except Exception as exc:
            print(f'  WARNING: backup {item["backup"]} was retained: {exc}',
                  file=sys.stderr, flush=True)

    print('\nFinished. Containers are running with the new image.', flush=True)
    if any('--enable-api' not in (x.get('Config', {}).get('Cmd') or []) for x in originals):
        print('NOTE: some containers do not have --enable-api. Original arguments were '
              'preserved; /api/metrics will be unavailable for those containers.', flush=True)
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception as exc:
        print(f'ERROR: {exc}', file=sys.stderr)
        sys.exit(1)
PY

printf '\nCurrent V380 containers:\n'
docker ps -a --format 'table {{.Names}}\t{{.Image}}\t{{.Status}}' | grep -E 'NAMES|v380' || true