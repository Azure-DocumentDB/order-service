"""Real Kestrel process regression tests; synthetic config, no database calls or credentials."""
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import unittest
from urllib.request import build_opener, ProxyHandler

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'src/OrderService/bin/Release/net10.0/OrderService.dll'


class BindingTests(unittest.TestCase):
    def environment(self):
        env = {key: value for key, value in os.environ.items()
               if not key.upper().startswith(('KESTREL', 'ASPNETCORE_', 'DOTNET_', 'DOCUMENTDB_', 'ORDER_API_'))}
        env.update({
            'DOCUMENTDB_URI': 'mongodb+srv://sample.mongocluster.cosmos.azure.com/',
            'DOCUMENTDB_ALLOWED_HOST': 'sample.mongocluster.cosmos.azure.com',
            'DOCUMENTDB_DATABASE': 'orders_demo_binding_test',
            'ORDER_API_TOKEN': 'synthetic_test_token_not_a_secret_12345678',
            'DOTNET_NOLOGO': 'true',
        })
        return env

    def start(self, env, args=(), cwd=ROOT):
        self.assertTrue(DLL.is_file(), 'Build Release before running binding tests')
        process = subprocess.Popen(['dotnet', str(DLL), *args], cwd=cwd, env=env,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.addCleanup(self.stop, process)
        return process

    @staticmethod
    def stop(process):
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
        process.communicate()

    def refused(self, process):
        stdout, stderr = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 1, 'Unsafe configured endpoint must refuse startup')
        self.assertEqual(stdout, '')
        self.assertIn('startup_or_seed_failed', stderr)

    def test_environment_kestrel_external_listener_refused(self):
        env = self.environment()
        env['Kestrel__Endpoints__Http__Url'] = 'http://0.0.0.0:5097'
        self.refused(self.start(env))

    def test_command_line_kestrel_external_listener_refused(self):
        self.refused(self.start(self.environment(), ['--Kestrel:Endpoints:Http:Url=http://0.0.0.0:5097']))

    def test_json_kestrel_additional_listener_refused(self):
        (ROOT / 'artifacts').mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=ROOT / 'artifacts') as folder:
            Path(folder, 'appsettings.json').write_text(json.dumps({'Kestrel': {'Endpoints': {
                'Local': {'Url': 'http://127.0.0.1:5080'}, 'External': {'Url': 'http://0.0.0.0:5097'}}}}))
            self.refused(self.start(self.environment(), cwd=folder))

    def test_aspnetcore_urls_cannot_override_actual_loopback_binding(self):
        # No silent pass if another sample instance already owns the fixed test port.
        with socket.socket() as available:
            available.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            available.bind(('127.0.0.1', 5080))
            available.listen(1) # Still fails if a live sample process already owns the port.
        env = self.environment()
        env['ASPNETCORE_URLS'] = 'http://0.0.0.0:5097'
        process = self.start(env)
        opener = build_opener(ProxyHandler({}))
        for _ in range(100):
            self.assertIsNone(process.poll(), 'Sample exited before loopback health was available')
            try:
                with opener.open('http://127.0.0.1:5080/health', timeout=.2) as response:
                    self.assertEqual(json.load(response)['status'], 'alive')
                break
            except OSError:
                time.sleep(.05)
        else:
            self.fail('Fixed loopback health endpoint not available')
        with socket.socket() as external_port:
            external_port.settimeout(.5)
            self.assertNotEqual(external_port.connect_ex(('127.0.0.1', 5097)), 0,
                                'ASPNETCORE_URLS unexpectedly opened its configured port')
        # UDP connect selects the local interface without sending a datagram. Verify the API
        # cannot be reached on that nonloopback interface even though loopback health works.
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as route:
            route.connect(('192.0.2.1', 9))
            local_ip = route.getsockname()[0]
        self.assertFalse(local_ip.startswith('127.'), 'Nonloopback interface required for this regression')
        with socket.socket() as external_interface:
            external_interface.settimeout(.5)
            self.assertNotEqual(external_interface.connect_ex((local_ip, 5080)), 0,
                                'Bearer API unexpectedly reachable on a nonloopback interface')


if __name__ == '__main__':
    unittest.main()
