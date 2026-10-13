'use strict';
const $ = id => document.getElementById(id);
const newKey = () => { $('key').value = crypto.randomUUID(); };
async function call(path, options = {}) {
  const response = await fetch(path, { ...options, headers: { Authorization: `Bearer ${$('token').value}`, ...(options.headers || {}) } });
  const body = await response.json();
  if (!response.ok) throw { status: response.status, ...body };
  return { status: response.status, ...body };
}
async function refresh() {
  const [inventory, orders] = await Promise.all([call('/api/inventory'), call('/api/orders')]);
  for (const [target, rows, label] of [
    ['inventory', inventory.items, p => `${p.name} (${p.id}): ${p.stock} available`],
    ['orders', orders.items, o => `${o.id} · ${o.items.map(i => `${i.quantity} × ${i.productId}`).join(', ')} · ${o.createdAtUtc}`]]) {
    $(target).replaceChildren(...rows.map(row => { const li = document.createElement('li'); li.textContent = label(row); return li; }));
  }
}
const report = value => { $('result').textContent = JSON.stringify(value, null, 2); };
$('refresh').addEventListener('click', () => refresh().catch(report));
$('new-key').addEventListener('click', newKey);
$('place').addEventListener('click', async () => {
  try {
    report(await call('/api/orders', { method: 'POST', headers: { 'Content-Type': 'application/json', 'Idempotency-Key': $('key').value },
      body: JSON.stringify({ items: [{ productId: 'notebook', quantity: Number($('notebook').value) }, { productId: 'pen', quantity: Number($('pen').value) }] }) }));
    await refresh();
  } catch (error) { report(error); }
});
newKey();
