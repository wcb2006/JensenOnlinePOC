// Säker DOM-byggare. All text läggs in som textnoder (motsvarar textContent),
// så data från API:t eller användaren tolkas aldrig som HTML. innerHTML används inte någonstans i appen.
export function h(tag, props = {}, ...children) {
  const el = document.createElement(tag);

  for (const [key, value] of Object.entries(props ?? {})) {
    if (value == null || value === false) continue;
    if (key.startsWith('on')) {
      // Händelser får bara vara funktioner – aldrig strängar som kan tolkas som kod.
      if (typeof value === 'function') el.addEventListener(key.slice(2).toLowerCase(), value);
    } else if (key === 'class') {
      el.className = value;
    } else if (key === 'value') {
      el.value = value;
    } else if (key === 'href' && !/^(#|\/)/.test(String(value))) {
      // Tillåt bara interna länkar – blockerar javascript:-URL:er.
      continue;
    } else if (value === true) {
      el.setAttribute(key, '');
    } else {
      el.setAttribute(key, String(value));
    }
  }

  for (const child of children.flat(Infinity)) {
    if (child == null || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

export function mount(target, ...nodes) {
  target.replaceChildren(...nodes.flat(Infinity).filter(Boolean));
}

const currency = new Intl.NumberFormat('sv-SE', { style: 'currency', currency: 'SEK' });
export const formatPrice = (value) => currency.format(value);

const dateTime = new Intl.DateTimeFormat('sv-SE', { dateStyle: 'short', timeStyle: 'short' });
export const formatDate = (iso) => dateTime.format(new Date(iso.endsWith('Z') ? iso : iso + 'Z'));
