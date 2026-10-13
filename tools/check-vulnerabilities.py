#!/usr/bin/env python3
"""Fail closed on NuGet advisory results, including transitives (list itself exits 0 on findings)."""
import json
import re
from urllib.parse import urlparse
import subprocess
import sys


def object_shape(value, allowed, required):
    if not isinstance(value, dict) or not required <= value.keys() or value.keys() - allowed:
        raise ValueError('Malformed or error-bearing NuGet object')
    if 'problems' in value and (not isinstance(value['problems'], list) or value['problems']):
        raise ValueError('NuGet advisory query reported problems')


def text(value):
    return isinstance(value, str) and bool(value.strip())


def validate_package(package, top_level):
    required = {'id', 'resolvedVersion'} | ({'requestedVersion'} if top_level else set())
    object_shape(package, {'id', 'resolvedVersion', 'requestedVersion', 'autoReferenced', 'vulnerabilities', 'problems'}, required)
    if (not text(package['id']) or not re.fullmatch(r'[A-Za-z0-9_.-]+', package['id'])
            or not text(package['resolvedVersion']) or not re.fullmatch(r'\d+\.\d+\.\d+(?:[.+-][0-9A-Za-z.-]+)?', package['resolvedVersion'])
            or ('requestedVersion' in package and not text(package['requestedVersion']))
            or ('autoReferenced' in package and not isinstance(package['autoReferenced'], bool))):
        raise ValueError('Malformed NuGet package identity/version')
    # Missing vulnerabilities is legitimately no findings; present falsey non-lists are not.
    advisories = package.get('vulnerabilities', [])
    if not isinstance(advisories, list):
        raise ValueError('Malformed vulnerability list')
    for advisory in advisories:
        object_shape(advisory, {'severity', 'advisoryurl'}, {'severity', 'advisoryurl'})
        if advisory['severity'] not in ('Low', 'Moderate', 'High', 'Critical') or not text(advisory['advisoryurl']):
            raise ValueError('Malformed vulnerability entry')
        url = urlparse(advisory['advisoryurl'])
        if url.scheme != 'https' or not url.netloc:
            raise ValueError('Malformed advisory URL')
    if advisories:
        raise ValueError('Vulnerable NuGet dependency detected')


def validate(report, expected_project=None):
    object_shape(report, {'version', 'parameters', 'sources', 'projects', 'problems'}, {'version', 'sources', 'projects'})
    if (type(report['version']) is not int or report['version'] != 1 or not isinstance(report['projects'], list)
            or len(report['projects']) != 1 or report['sources'] != ['https://api.nuget.org/v3/index.json']
            or ('parameters' in report and not text(report['parameters']))):
        raise ValueError('Incomplete NuGet advisory response')
    for project in report['projects']:
        object_shape(project, {'path', 'frameworks', 'problems'}, {'path'})
        if not text(project['path']):
            raise ValueError('Incomplete project advisory response')
        if expected_project is not None:
            from pathlib import Path
            if Path(project['path']).resolve() != Path(expected_project).resolve():
                raise ValueError('Unexpected project in advisory response')
        # .NET 10 --vulnerable omits frameworks entirely when there are no findings.
        # This is accepted only in the verified vulnerable-query envelope; nonzero CLI,
        # stderr warnings, unknown fields and feed problems remain fatal in main().
        if 'frameworks' not in project:
            if report.get('parameters') != '--vulnerable --include-transitive':
                raise ValueError('Missing vulnerable-query envelope')
            continue
        if not isinstance(project['frameworks'], list) or not project['frameworks']:
            raise ValueError('Incomplete project advisory response')
        for framework in project['frameworks']:
            object_shape(framework, {'framework', 'topLevelPackages', 'transitivePackages', 'problems'}, {'framework', 'topLevelPackages'})
            if framework['framework'] != 'net10.0' or not isinstance(framework['topLevelPackages'], list):
                raise ValueError('Incomplete framework advisory response')
            for group in ('topLevelPackages', 'transitivePackages'):
                packages = framework.get(group, [])
                if not isinstance(packages, list):
                    raise ValueError('Malformed advisory packages')
                for package in packages:
                    validate_package(package, group == 'topLevelPackages')


def main():
    for project in ('src/OrderService/OrderService.csproj', 'tests/OrderService.Tests/OrderService.Tests.csproj', 'tests/OrderService.Probes/OrderService.Probes.csproj'):
        command = ['dotnet', 'list', project, 'package', '--vulnerable', '--include-transitive',
                   '--source', 'https://api.nuget.org/v3/index.json', '--format', 'json']
        result = subprocess.run(command, capture_output=True, text=True, check=True, timeout=180)
        if result.stderr.strip():
            raise ValueError('NuGet query emitted warnings or errors')
        validate(json.loads(result.stdout), project)
        print(f'PASS current-feed NuGet direct + transitive audit: {project}')


if __name__ == '__main__':
    try:
        main()
    except (ValueError, TypeError, KeyError, AttributeError, OSError, subprocess.SubprocessError):
        print('FAIL NuGet advisory gate: vulnerability, incomplete result, or feed/tool failure', file=sys.stderr)
        sys.exit(1)
