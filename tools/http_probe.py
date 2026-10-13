#!/usr/bin/env python3
"""Opt-in HTTP contract + real process restart persistence proof. Standard library only."""
import argparse
import json
import os
from pathlib import Path
import sys
from urllib.error import HTTPError
from urllib.parse import urlsplit
from urllib.request import Request, build_opener, HTTPRedirectHandler
import uuid


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError('redirect_refused')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--base-url', default='http://127.0.0.1:5080')
    parser.add_argument('--phase', choices=('before', 'after'), required=True)
    parser.add_argument('--state-file', type=Path, required=True)
    args = parser.parse_args()
    target = urlsplit(args.base_url)
    if (os.environ.get('RUN_LIVE_TESTS') != 'true' or os.environ.get('DOCUMENTDB_ALLOW_WRITES') != 'true'
            or target.scheme != 'http' or target.hostname != '127.0.0.1' or target.username
            or target.password or target.path not in ('', '/') or target.query or target.fragment):
        raise ValueError('explicit_live_opt_in_and_loopback_required')
    token = os.environ.get('ORDER_API_TOKEN', '')
    if len(token) < 32:
        raise ValueError('operator_token_required')
    opener = build_opener(NoRedirect())
    checks = []

    def check(condition, name):
        if not condition:
            raise ValueError(name)
        checks.append(name)

    def call(path, payload=None, key=None, authenticated=True):
        headers = {'Authorization': 'Bearer ' + token} if authenticated else {}
        if key is not None:
            headers['Idempotency-Key'] = key
        data = None
        if payload is not None:
            data = json.dumps(payload).encode()
            headers['Content-Type'] = 'application/json'
        req = Request(args.base_url.rstrip('/') + path, data=data, headers=headers)
        try:
            response = opener.open(req, timeout=35)
        except HTTPError as error:
            response = error
        with response:
            return response.status, json.load(response)

    def stocks():
        # Probe inventory may have >100 products. Exercise stable bounded pagination, not truncation.
        values = {}
        cursor = None
        for _ in range(100):
            status, body = call('/api/inventory?limit=100' + ('&cursor=' + cursor if cursor else ''))
            check(status == 200, 'inventory_page')
            values.update({p['id']: p['stock'] for p in body['items']})
            cursor = body['nextCursor']
            if cursor is None:
                return {key: values[key] for key in ('notebook', 'pen')}
        raise ValueError('pagination_budget_exceeded')

    body = {'items': [{'productId': 'notebook', 'quantity': 1}, {'productId': 'pen', 'quantity': 1}]}
    check(call('/api/orders', authenticated=False)[0] == 401, 'anonymous_rejected')
    health_status, health = call('/health')
    check(health_status == 200 and isinstance(health.get('instanceId'), str), 'instance_id_available')
    if args.phase == 'before':
        if args.state_file.exists():
            raise ValueError('state_file_already_exists_use_new_path')
        key = 'restart_' + uuid.uuid4().hex
        before = stocks()
        status, first = call('/api/orders', body, key)
        check(status == 201 and not first['replayed'], 'created_201')
        status, replay = call('/api/orders', body, key)
        check(status == 200 and replay['replayed'] and first['order']['orderId'] == replay['order']['orderId'], 'replay_200_same_order')
        status, conflict = call('/api/orders', {'items': [{'productId': 'pen', 'quantity': 2}]}, key)
        check(status == 409 and conflict['code'] == 'idempotency_conflict', 'mismatch_409')
        after = stocks()
        check(all(after[p] == before[p] - 1 for p in before), 'two_products_deduct_once')
        state = {'key': key, 'orderId': first['order']['orderId'], 'stocks': after, 'instanceId': health['instanceId']}
        # Synthetic identifiers only, no token or destination. Still write owner-only and refuse overwrite.
        fd = os.open(args.state_file, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as file:
            json.dump(state, file)
    else:
        state = json.loads(args.state_file.read_text())
        check(health['instanceId'] != state['instanceId'], 'actual_process_restart')
        status, replay = call('/api/orders', body, state['key'])
        check(status == 200 and replay['replayed'] and replay['order']['orderId'] == state['orderId'], 'persistent_replay_after_restart')
        check(stocks() == state['stocks'], 'restart_replay_changes_no_stock')
    print(json.dumps({'status': 'passed', 'phase': args.phase, 'checksPassed': len(checks), 'checks': checks}))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # No raw exception text: urllib errors could otherwise echo credentials/destinations.
        print(json.dumps({'status': 'failed', 'code': 'http_probe_failed', 'exceptionType': type(error).__name__}))
        sys.exit(1)
