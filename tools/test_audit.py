import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('audit', Path(__file__).with_name('check-vulnerabilities.py'))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class AuditTests(unittest.TestCase):
    def clean(self):
        return {'version': 1, 'sources': ['https://api.nuget.org/v3/index.json'], 'projects': [{'path': 'test.csproj', 'frameworks': [{'framework': 'net10.0', 'topLevelPackages': []}]}]}

    def test_clean_result(self):
        audit.validate(self.clean())

    def test_incomplete_response(self):
        for report in ({}, {'version': 1}, {'version': 1, 'projects': []}):
            with self.assertRaises(ValueError):
                audit.validate(report)

    def test_feed_problem(self):
        report = self.clean(); report['problems'] = ['unreachable']
        with self.assertRaises(ValueError):
            audit.validate(report)

    def test_direct_and_transitive_findings(self):
        for group in ('topLevelPackages', 'transitivePackages'):
            report = self.clean()
            report['projects'][0]['frameworks'][0][group] = [{'id': 'bad', 'vulnerabilities': [{'severity': 'Low'}]}]
            with self.assertRaises(ValueError):
                audit.validate(report)

    def test_missing_frameworks(self):
        report = self.clean(); report['projects'][0].pop('frameworks')
        with self.assertRaises(ValueError):
            audit.validate(report)

    def test_actual_dotnet10_clean_sparse_response(self):
        audit.validate({'version': 1, 'parameters': '--vulnerable --include-transitive',
                        'sources': ['https://api.nuget.org/v3/index.json'], 'projects': [{'path': 'test.csproj'}]}, 'test.csproj')

    def test_unexpected_project_rejected(self):
        with self.assertRaises(ValueError):
            audit.validate(self.clean(), 'different.csproj')

    def test_present_empty_frameworks_rejected(self):
        report = self.clean(); report['projects'][0]['frameworks'] = []
        with self.assertRaises(ValueError):
            audit.validate(report)

    def test_nested_feed_problem(self):
        report = self.clean(); report['projects'][0]['frameworks'][0]['problems'] = ['failure']
        with self.assertRaises(ValueError):
            audit.validate(report)

    def test_unexpected_feed(self):
        report = self.clean(); report['sources'] = ['https://untrusted.example/']
        with self.assertRaises(ValueError):
            audit.validate(report)

    def test_missing_package_arrays(self):
        report = self.clean(); report['projects'][0]['frameworks'][0].pop('topLevelPackages')
        with self.assertRaises(ValueError):
            audit.validate(report)

    def package_report(self, package, group='transitivePackages'):
        report = self.clean(); report['projects'][0]['frameworks'][0][group] = [package]
        return report

    def test_missing_vulnerabilities_is_valid(self):
        audit.validate(self.package_report({'id': 'Example.Package', 'resolvedVersion': '1.2.3'}))
        audit.validate(self.package_report({'id': 'Example.Package', 'resolvedVersion': '1.2.3', 'requestedVersion': '[1.0.0, )', 'vulnerabilities': []}, 'topLevelPackages'))

    def test_package_errors_rejected(self):
        for key, value in [('problems', ['feed failure']), ('error', {'code': 503}), ('status', 503), ('errors', []), ('problems', None)]:
            with self.subTest(key=key), self.assertRaises(ValueError):
                audit.validate(self.package_report({'id': 'x', 'resolvedVersion': '1.0.0', key: value}))

    def test_falsey_malformed_vulnerability_lists(self):
        for value in (None, {}, False, '', 0):
            with self.subTest(value=value), self.assertRaises(ValueError):
                audit.validate(self.package_report({'id': 'x', 'resolvedVersion': '1.0.0', 'vulnerabilities': value}))

    def test_package_identity_version_schema(self):
        for package in (None, [], {}, {'id': 'x'}, {'id': False, 'resolvedVersion': '1.0.0'},
                        {'id': 'x', 'resolvedVersion': ''}, {'id': 'x', 'resolvedVersion': False},
                        {'id': 'x', 'resolvedVersion': 'not-a-version'}):
            with self.subTest(package=package), self.assertRaises(ValueError):
                audit.validate(self.package_report(package))
        with self.assertRaises(ValueError):
            audit.validate(self.package_report({'id': 'x', 'resolvedVersion': '1.0.0'}, 'topLevelPackages'))

    def test_advisory_schema_and_actual_findings(self):
        for entry in (None, {}, {'severity': 'Low'}, {'severity': False, 'advisoryurl': 'https://example.test/advisory'},
                      {'severity': 'Low', 'advisoryurl': False}, {'severity': 'Low', 'advisoryurl': 'not-a-url'},
                      {'severity': 'Low', 'advisoryurl': 'https://example.test/advisory'}):
            with self.subTest(entry=entry), self.assertRaises(ValueError):
                audit.validate(self.package_report({'id': 'x', 'resolvedVersion': '1.0.0', 'vulnerabilities': [entry]}))

    def test_errors_at_every_object_level(self):
        for level in ('report', 'project', 'framework'):
            report = self.clean()
            target = report if level == 'report' else report['projects'][0] if level == 'project' else report['projects'][0]['frameworks'][0]
            target['error'] = {'code': 503}
            with self.subTest(level=level), self.assertRaises(ValueError):
                audit.validate(report)


if __name__ == '__main__':
    unittest.main()
