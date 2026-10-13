import { h, mount, formatPrice, formatDate } from './dom.js';
import { api, session, refreshSession } from './api.js';

const main = document.getElementById('main');
const nav = document.getElementById('nav');
const toastEl = document.getElementById('toast');

// ---------------------------------------------------------------- Hjälpfunktioner

let toastTimer;
function toast(message, kind = 'info') {
  toastEl.textContent = message;
  toastEl.className = `toast ${kind}`;
  toastEl.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { toastEl.hidden = true; }, 4000);
}

// Visar fel från API:t (ProblemDetails). Endast generiska meddelanden kommer från servern – aldrig stacktraces.
function errorBox(err) {
  const fieldMessages = Object.values(err.fieldErrors ?? {}).flat();
  return h('div', { class: 'alert', role: 'alert' },
    h('strong', {}, err.title && err.message !== err.title ? err.title : 'Fel'),
    h('p', {}, err.message),
    fieldMessages.length ? h('ul', {}, fieldMessages.map((m) => h('li', {}, m))) : null,
    err.traceId ? h('p', { class: 'muted small' }, `Trace-id: ${err.traceId}`) : null);
}

function field(label, input, hint) {
  return h('label', { class: 'field' }, h('span', {}, label), input, hint ? h('small', { class: 'muted' }, hint) : null);
}

function routeInfo() {
  const [path, query] = location.hash.replace(/^#\/?/, '').split('?');
  return { route: path || 'products', params: new URLSearchParams(query ?? '') };
}

// Knapp som kräver två klick – ersätter confirm()-dialoger.
function confirmButton(label, onConfirm) {
  let armed = false;
  const button = h('button', {
    type: 'button',
    class: 'danger small',
    onclick: async () => {
      if (!armed) {
        armed = true;
        button.textContent = 'Bekräfta?';
        setTimeout(() => { armed = false; button.textContent = label; }, 3000);
        return;
      }
      button.disabled = true;
      await onConfirm();
    },
  }, label);
  return button;
}

// ---------------------------------------------------------------- Kundvagn
// Kundvagnen sparas i sessionStorage (bara produkt-id, namn, visat pris och antal).
// Priset här är bara en förhandsvisning – servern räknar alltid om priset från databasen.

const CART_KEY = 'jo_cart';
const cart = {
  items() {
    try { return JSON.parse(sessionStorage.getItem(CART_KEY)) ?? []; } catch { return []; }
  },
  save(items) {
    try { sessionStorage.setItem(CART_KEY, JSON.stringify(items)); } catch { /* ignoreras */ }
    renderNav();
  },
  add(product) {
    const items = cart.items();
    const existing = items.find((i) => i.productId === product.id);
    if (existing) existing.quantity = Math.min(existing.quantity + 1, 10);
    else items.push({ productId: product.id, name: product.name, price: product.price, quantity: 1 });
    cart.save(items);
  },
  setQuantity(productId, quantity) {
    cart.save(cart.items().map((i) => (i.productId === productId ? { ...i, quantity } : i)));
  },
  remove(productId) { cart.save(cart.items().filter((i) => i.productId !== productId)); },
  clear() { cart.save([]); },
  count() { return cart.items().reduce((sum, i) => sum + i.quantity, 0); },
};

// ---------------------------------------------------------------- Navigation

function renderNav() {
  const { route } = routeInfo();
  const link = (href, text) => h('a', { href, class: href === `#/${route}` ? 'active' : null }, text);

  const links = [link('#/products', 'Produkter'), link('#/cart', `Kundvagn (${cart.count()})`)];
  if (session.isLoggedIn) links.push(link('#/orders', 'Mina order'));
  // Admin-länken döljs för kunder – men det är bara UX. Den riktiga kontrollen görs i API:t.
  if (session.isAdmin) links.push(link('#/admin', 'Admin'));

  const account = session.isLoggedIn
    ? h('div', { class: 'account' },
        h('span', { class: 'muted' }, session.user.email),
        h('span', { class: `badge ${session.isAdmin ? 'admin' : ''}` }, session.user.role),
        h('button', { type: 'button', class: 'secondary small', onclick: logout }, 'Logga ut'))
    : h('div', { class: 'account' }, link('#/login', 'Logga in'), link('#/register', 'Skapa konto'));

  mount(nav, h('div', { class: 'links' }, links), account);
}

async function logout() {
  await api.logout().catch(() => {});
  cart.clear();
  toast('Du är utloggad.');
  location.hash = '#/products';
  render();
}

// ---------------------------------------------------------------- Produkter

function productCard(product) {
  return h('article', { class: 'card product' },
    h('span', { class: 'muted small' }, product.category),
    h('h2', {}, product.name),
    h('p', { class: 'description' }, product.description),
    h('div', { class: 'row' },
      h('strong', { class: 'price' }, formatPrice(product.price)),
      h('span', { class: 'muted small' }, product.stock > 0 ? `${product.stock} i lager` : 'Slut i lager')),
    h('button', {
      type: 'button',
      disabled: product.stock < 1,
      onclick: () => { cart.add(product); toast(`${product.name} lades i kundvagnen.`, 'success'); },
    }, 'Lägg i kundvagn'));
}

async function viewProducts() {
  const search = routeInfo().params.get('q') ?? '';
  const input = h('input', { type: 'search', name: 'q', value: search, maxlength: 50, placeholder: 'Sök produkter…', 'aria-label': 'Sök produkter' });
  const form = h('form', {
    class: 'search',
    onsubmit: (e) => {
      e.preventDefault();
      const q = input.value.trim();
      location.hash = q ? `#/products?q=${encodeURIComponent(q)}` : '#/products';
    },
  }, input, h('button', { type: 'submit' }, 'Sök'));

  const products = await api.products(search);
  mount(main, h('section', {},
    h('h1', {}, 'Produkter'),
    form,
    search ? h('p', { class: 'muted' }, `Sökresultat för: `, h('q', {}, search)) : null,
    products.length ? h('div', { class: 'grid' }, products.map(productCard)) : h('p', { class: 'muted' }, 'Inga produkter hittades.')));
}

// ---------------------------------------------------------------- Kundvagn

function viewCart() {
  const items = cart.items();
  if (!items.length) {
    mount(main, h('section', { class: 'card narrow' }, h('h1', {}, 'Kundvagn'), h('p', {}, 'Kundvagnen är tom. '), h('a', { href: '#/products' }, 'Till produkterna')));
    return;
  }

  const errors = h('div');
  const rows = items.map((item) => h('tr', {},
    h('td', {}, item.name),
    h('td', {}, formatPrice(item.price)),
    h('td', {}, h('input', {
      type: 'number', min: 1, max: 10, value: item.quantity, class: 'qty', 'aria-label': `Antal ${item.name}`,
      onchange: (e) => {
        const qty = Math.max(1, Math.min(10, Number.parseInt(e.target.value, 10) || 1));
        cart.setQuantity(item.productId, qty);
        viewCart();
      },
    })),
    h('td', {}, formatPrice(item.price * item.quantity)),
    h('td', {}, h('button', { type: 'button', class: 'secondary small', onclick: () => { cart.remove(item.productId); viewCart(); } }, 'Ta bort'))));

  const total = items.reduce((sum, i) => sum + i.price * i.quantity, 0);

  const placeOrder = async (e) => {
    if (!session.isLoggedIn) {
      toast('Logga in för att lägga en order.');
      location.hash = '#/login?next=cart';
      return;
    }
    e.target.disabled = true;
    try {
      const order = await api.createOrder(items.map((i) => ({ productId: i.productId, quantity: i.quantity })));
      cart.clear();
      toast(`Order #${order.id} skapad (${formatPrice(order.total)}).`, 'success');
      location.hash = '#/orders';
    } catch (err) {
      mount(errors, errorBox(err));
      e.target.disabled = false;
    }
  };

  mount(main, h('section', { class: 'card' },
    h('h1', {}, 'Kundvagn'),
    h('table', {},
      h('thead', {}, h('tr', {}, ['Produkt', 'Pris', 'Antal', 'Summa', ''].map((t) => h('th', {}, t)))),
      h('tbody', {}, rows)),
    h('p', { class: 'total' }, 'Beräknad total: ', h('strong', {}, formatPrice(total))),
    h('p', { class: 'muted small' }, 'Slutligt pris räknas fram av servern. Betalning sker via extern betalleverantör (mockad i POC).'),
    errors,
    h('button', { type: 'button', onclick: placeOrder }, 'Lägg order')));
}

// ---------------------------------------------------------------- Order

function statusLabel(status) {
  return { Created: 'Mottagen', Paid: 'Betald', Shipped: 'Skickad', Cancelled: 'Avbruten' }[status] ?? status;
}

function orderCard(order, adminControls) {
  return h('article', { class: 'card order' },
    h('div', { class: 'row' },
      h('h2', {}, `Order #${order.id}`),
      h('span', { class: `badge status-${order.status.toLowerCase()}` }, statusLabel(order.status))),
    h('p', { class: 'muted small' },
      formatDate(order.createdAtUtc),
      order.customerEmail ? ` · Kund: ${order.customerEmail}` : ''),
    h('table', {},
      h('thead', {}, h('tr', {}, ['Produkt', 'Pris', 'Antal', 'Summa'].map((t) => h('th', {}, t)))),
      h('tbody', {}, order.items.map((i) => h('tr', {},
        h('td', {}, i.productName), h('td', {}, formatPrice(i.unitPrice)), h('td', {}, i.quantity), h('td', {}, formatPrice(i.lineTotal)))))),
    h('div', { class: 'row' },
      h('p', { class: 'total' }, 'Total: ', h('strong', {}, formatPrice(order.total))),
      adminControls ?? null));
}

async function viewOrders() {
  const orders = await api.myOrders();
  mount(main, h('section', {},
    h('h1', {}, 'Mina order'),
    orders.length ? orders.map((o) => orderCard(o)) : h('p', { class: 'muted' }, 'Du har inga order ännu.')));
}

// ---------------------------------------------------------------- Inloggning / registrering

function viewLogin() {
  const next = routeInfo().params.get('next');
  const errors = h('div');
  const email = h('input', { type: 'email', name: 'email', required: true, maxlength: 254, autocomplete: 'username' });
  const password = h('input', { type: 'password', name: 'password', required: true, maxlength: 128, autocomplete: 'current-password' });
  const submit = h('button', { type: 'submit' }, 'Logga in');

  const form = h('form', {
    class: 'card narrow',
    onsubmit: async (e) => {
      e.preventDefault();
      submit.disabled = true;
      mount(errors);
      try {
        const user = await api.login(email.value.trim(), password.value);
        toast(`Välkommen, ${user.email}!`, 'success');
        location.hash = next && /^[a-z]+$/.test(next) ? `#/${next}` : '#/products';
      } catch (err) {
        mount(errors, errorBox(err));
        password.value = '';
      } finally {
        submit.disabled = false;
      }
    },
  },
  h('h1', {}, 'Logga in'),
  field('E-post', email),
  field('Lösenord', password),
  errors,
  submit,
  h('p', { class: 'muted small' }, 'Inget konto? ', h('a', { href: '#/register' }, 'Skapa konto')));

  mount(main, form);
  email.focus();
}

// Samma regler som servern – här bara för snabb återkoppling. Servern validerar alltid igen.
function passwordProblems(pw, email) {
  const problems = [];
  if (pw.length < 12) problems.push('minst 12 tecken');
  if (!/[A-ZÅÄÖ]/.test(pw)) problems.push('en versal');
  if (!/[a-zåäö]/.test(pw)) problems.push('en gemen');
  if (!/\d/.test(pw)) problems.push('en siffra');
  if (!/[^\p{L}\p{N}]/u.test(pw)) problems.push('ett specialtecken');
  const local = email.split('@')[0];
  if (local.length >= 3 && pw.toLowerCase().includes(local.toLowerCase())) problems.push('får inte innehålla e-postadressen');
  return problems;
}

function viewRegister() {
  const errors = h('div');
  const email = h('input', { type: 'email', name: 'email', required: true, maxlength: 254, autocomplete: 'username' });
  const password = h('input', { type: 'password', name: 'password', required: true, minlength: 12, maxlength: 128, autocomplete: 'new-password' });
  const confirm = h('input', { type: 'password', name: 'confirm', required: true, maxlength: 128, autocomplete: 'new-password' });
  const hint = h('small', { class: 'muted' }, 'Minst 12 tecken med versal, gemen, siffra och specialtecken.');
  const submit = h('button', { type: 'submit' }, 'Skapa konto');

  password.addEventListener('input', () => {
    const problems = passwordProblems(password.value, email.value);
    hint.textContent = problems.length ? `Saknas: ${problems.join(', ')}` : 'Lösenordet uppfyller kraven ✓';
    hint.className = problems.length ? 'muted' : 'ok';
  });

  const form = h('form', {
    class: 'card narrow',
    onsubmit: async (e) => {
      e.preventDefault();
      mount(errors);
      const problems = passwordProblems(password.value, email.value);
      if (problems.length) { mount(errors, errorBox({ message: `Lösenordet uppfyller inte kraven: ${problems.join(', ')}.` })); return; }
      if (password.value !== confirm.value) { mount(errors, errorBox({ message: 'Lösenorden matchar inte.' })); return; }

      submit.disabled = true;
      try {
        await api.register(email.value.trim(), password.value);
        await api.login(email.value.trim(), password.value);
        toast('Kontot är skapat och du är inloggad.', 'success');
        location.hash = '#/products';
      } catch (err) {
        mount(errors, errorBox(err));
      } finally {
        submit.disabled = false;
      }
    },
  },
  h('h1', {}, 'Skapa konto'),
  field('E-post', email),
  h('label', { class: 'field' }, h('span', {}, 'Lösenord'), password, hint),
  field('Bekräfta lösenord', confirm),
  errors,
  submit);

  mount(main, form);
  email.focus();
}

// ---------------------------------------------------------------- Admin

const NAME_PATTERN = /^[\p{L}\p{N} .,:!?&()+/\-]+$/u;
const CATEGORY_PATTERN = /^[\p{L}\p{N} &\-]+$/u;

async function viewAdmin() {
  const tab = routeInfo().params.get('tab') ?? 'products';
  const tabs = [['products', 'Produkter'], ['orders', 'Alla order'], ['users', 'Användare'], ['audit', 'Säkerhetslogg']];
  const content = h('div', {}, h('p', { class: 'muted' }, 'Laddar…'));

  mount(main, h('section', {},
    h('h1', {}, 'Administration'),
    h('div', { class: 'tabs', role: 'tablist' }, tabs.map(([id, label]) =>
      h('a', { href: `#/admin?tab=${id}`, role: 'tab', class: id === tab ? 'active' : null, 'aria-selected': String(id === tab) }, label))),
    content));

  const views = { products: adminProducts, orders: adminOrders, users: adminUsers, audit: adminAudit };
  await (views[tab] ?? adminProducts)(content);
}

async function adminProducts(container) {
  const products = await api.products();
  const errors = h('div');
  let editingId = null;

  const inputs = {
    name: h('input', { name: 'name', required: true, minlength: 2, maxlength: 100 }),
    category: h('input', { name: 'category', required: true, minlength: 2, maxlength: 50 }),
    price: h('input', { name: 'price', type: 'number', required: true, min: 0.01, max: 1000000, step: 0.01 }),
    stock: h('input', { name: 'stock', type: 'number', required: true, min: 0, max: 100000, step: 1 }),
    description: h('textarea', { name: 'description', maxlength: 1000, rows: 3 }),
  };
  const heading = h('h2', {}, 'Ny produkt');
  const submit = h('button', { type: 'submit' }, 'Spara');
  const cancel = h('button', { type: 'button', class: 'secondary', hidden: true, onclick: () => resetForm() }, 'Avbryt');

  function resetForm() {
    editingId = null;
    Object.values(inputs).forEach((i) => { i.value = ''; });
    heading.textContent = 'Ny produkt';
    cancel.hidden = true;
    mount(errors);
  }

  function edit(p) {
    editingId = p.id;
    inputs.name.value = p.name;
    inputs.category.value = p.category;
    inputs.price.value = p.price;
    inputs.stock.value = p.stock;
    inputs.description.value = p.description;
    heading.textContent = `Redigera produkt #${p.id}`;
    cancel.hidden = false;
    inputs.name.focus();
  }

  const form = h('form', {
    class: 'card',
    onsubmit: async (e) => {
      e.preventDefault();
      mount(errors);
      const product = {
        name: inputs.name.value.trim(),
        category: inputs.category.value.trim(),
        price: Number(inputs.price.value),
        stock: Number.parseInt(inputs.stock.value, 10),
        description: inputs.description.value.trim(),
      };
      // Klientvalidering (UX). Samma regler kontrolleras på servern.
      const problems = [];
      if (!NAME_PATTERN.test(product.name)) problems.push('Namnet innehåller otillåtna tecken.');
      if (!CATEGORY_PATTERN.test(product.category)) problems.push('Kategorin innehåller otillåtna tecken.');
      if (problems.length) { mount(errors, errorBox({ message: problems.join(' ') })); return; }

      submit.disabled = true;
      try {
        if (editingId) await api.updateProduct(editingId, product);
        else await api.createProduct(product);
        toast('Produkten sparades.', 'success');
        await adminProducts(container);
      } catch (err) {
        mount(errors, errorBox(err));
        submit.disabled = false;
      }
    },
  },
  heading,
  h('div', { class: 'form-grid' },
    field('Namn', inputs.name),
    field('Kategori', inputs.category),
    field('Pris (SEK)', inputs.price),
    field('Lager', inputs.stock)),
  field('Beskrivning', inputs.description),
  errors,
  h('div', { class: 'row start' }, submit, cancel));

  const table = h('table', {},
    h('thead', {}, h('tr', {}, ['#', 'Namn', 'Kategori', 'Pris', 'Lager', ''].map((t) => h('th', {}, t)))),
    h('tbody', {}, products.map((p) => h('tr', {},
      h('td', {}, p.id), h('td', {}, p.name), h('td', {}, p.category), h('td', {}, formatPrice(p.price)), h('td', {}, p.stock),
      h('td', { class: 'actions' },
        h('button', { type: 'button', class: 'secondary small', onclick: () => edit(p) }, 'Ändra'),
        confirmButton('Ta bort', async () => {
          try {
            await api.deleteProduct(p.id);
            toast('Produkten togs bort.', 'success');
            await adminProducts(container);
          } catch (err) { toast(err.message, 'error'); }
        }))))));

  mount(container, form, h('div', { class: 'card' }, h('h2', {}, 'Produkter'), table));
}

async function adminOrders(container) {
  const orders = await api.adminOrders();
  if (!orders.length) { mount(container, h('p', { class: 'muted' }, 'Inga order ännu.')); return; }

  mount(container, orders.map((order) => {
    const select = h('select', { 'aria-label': `Status för order ${order.id}` },
      ['Created', 'Paid', 'Shipped', 'Cancelled'].map((s) => h('option', { value: s, selected: s === order.status }, statusLabel(s))));
    const save = h('button', {
      type: 'button', class: 'secondary small',
      onclick: async () => {
        try { await api.setOrderStatus(order.id, select.value); toast(`Order #${order.id} uppdaterad.`, 'success'); await adminOrders(container); }
        catch (err) { toast(err.message, 'error'); }
      },
    }, 'Spara status');
    return orderCard(order, h('div', { class: 'row' }, select, save));
  }));
}

async function adminUsers(container) {
  const users = await api.adminUsers();
  const rows = users.map((u) => {
    const isSelf = u.id === session.user.id;
    const role = h('select', { disabled: isSelf, 'aria-label': `Roll för ${u.email}` },
      ['Customer', 'Admin'].map((r) => h('option', { value: r, selected: r === u.role }, r)));
    role.addEventListener('change', async () => {
      try { await api.setUserRole(u.id, role.value); toast(`Rollen för ${u.email} ändrades till ${role.value}.`, 'success'); }
      catch (err) { toast(err.message, 'error'); }
      await adminUsers(container);
    });
    const toggle = h('button', {
      type: 'button', class: u.isDisabled ? 'secondary small' : 'danger small', disabled: isSelf,
      onclick: async () => {
        try { await api.setUserDisabled(u.id, !u.isDisabled); toast(`${u.email} ${u.isDisabled ? 'aktiverades' : 'spärrades'}.`, 'success'); }
        catch (err) { toast(err.message, 'error'); }
        await adminUsers(container);
      },
    }, u.isDisabled ? 'Aktivera' : 'Spärra');

    const status = u.isDisabled ? 'Spärrad' : u.isLockedOut ? 'Tillfälligt låst' : 'Aktiv';
    return h('tr', {},
      h('td', {}, u.id), h('td', {}, u.email, isSelf ? ' (du)' : ''), h('td', {}, role),
      h('td', {}, h('span', { class: `badge ${u.isDisabled || u.isLockedOut ? 'warn' : ''}` }, status)),
      h('td', {}, u.failedLoginCount), h('td', {}, formatDate(u.createdAtUtc)), h('td', {}, toggle));
  });

  mount(container, h('div', { class: 'card' },
    h('h2', {}, 'Användare'),
    h('p', { class: 'muted small' }, 'Rolländring eller spärr ogiltigförklarar användarens tokens direkt (security stamp).'),
    h('table', {},
      h('thead', {}, h('tr', {}, ['#', 'E-post', 'Roll', 'Status', 'Misslyckade inlogg', 'Skapad', ''].map((t) => h('th', {}, t)))),
      h('tbody', {}, rows))));
}

async function adminAudit(container) {
  const logs = await api.auditLogs();
  mount(container, h('div', { class: 'card' },
    h('h2', {}, 'Säkerhetslogg (audit log)'),
    h('table', { class: 'compact' },
      h('thead', {}, h('tr', {}, ['Tid', 'Händelse', 'Användare', 'IP', 'Detaljer'].map((t) => h('th', {}, t)))),
      h('tbody', {}, logs.map((l) => h('tr', { class: l.success ? null : 'failed' },
        h('td', {}, formatDate(l.timestampUtc)),
        h('td', {}, h('span', { class: `badge ${l.success ? '' : 'warn'}` }, l.action)),
        h('td', {}, l.email ?? '–'),
        h('td', {}, l.ipAddress ?? '–'),
        h('td', {}, l.details ?? '')))))));
}

// ---------------------------------------------------------------- Router

const routes = { products: viewProducts, cart: viewCart, orders: viewOrders, login: viewLogin, register: viewRegister, admin: viewAdmin };
const requiresLogin = new Set(['orders', 'admin']);

async function render() {
  const { route } = routeInfo();
  renderNav();

  if (requiresLogin.has(route) && !session.isLoggedIn) {
    location.hash = `#/login?next=${route}`;
    return;
  }
  if (route === 'admin' && !session.isAdmin) {
    mount(main, h('section', { class: 'card narrow' }, h('h1', {}, 'Åtkomst nekad'), h('p', {}, 'Sidan kräver administratörsbehörighet.')));
    return;
  }

  const view = routes[route];
  if (!view) {
    mount(main, h('section', { class: 'card narrow' }, h('h1', {}, 'Sidan finns inte'), h('a', { href: '#/products' }, 'Till startsidan')));
    return;
  }

  try {
    await view();
  } catch (err) {
    if (err.status === 401) {
      toast('Din session har gått ut. Logga in igen.');
      location.hash = `#/login?next=${route}`;
      return;
    }
    mount(main, errorBox(err));
  }
}

session.onChange(renderNav);
window.addEventListener('hashchange', render);

// Återställ session från HttpOnly-cookien (om användaren var inloggad) innan första rendering.
await refreshSession();
render();
