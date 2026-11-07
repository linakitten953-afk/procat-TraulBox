// главный объект с данными сайта
const S = {
  user: null,
  cats: [],
  stickers: [],
  products: [],
  cart: JSON.parse(localStorage.getItem('cart') || '[]')
};

// короткие функции которыми пользуемся везде
const view = document.getElementById('view');
const $ = (s, r = document) => r.querySelector(s);
const esc = v => String(v ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const money = n => Number(n || 0).toLocaleString('ru-RU', { maximumFractionDigits: 2 }) + ' ₽';
const pad = n => String(n).padStart(2, '0');

// дата из базы в красивый вид
function fmtDate(s, withTime) {
  if (!s) return '';
  const [d, t] = s.split(' ');
  const r = d.split('-').reverse().join('.');
  return withTime && t ? r + ' ' + t.slice(0, 5) : r;
}

// подписи для значений из базы
const PAY = { card: 'Картой при получении', cash: 'Наличными', online: 'Онлайн' };
const DEL = { pickup: 'Самовывоз', delivery: 'Доставка' };
const STATUS = { new: 'Новый', cooking: 'Готовится', ready: 'Готов', done: 'Выдан', cancelled: 'Отменен' };

// если картинка не загрузилась, показываем название вместо нее
window.imgFail = img => {
  const d = document.createElement('div');
  d.className = 'img-fallback';
  d.textContent = img.alt;
  img.replaceWith(d);
};
const picHtml = (url, name) => url
  ? `<img src="${esc(url)}" alt="${esc(name)}" loading="lazy" referrerpolicy="no-referrer" onerror="imgFail(this)">`
  : `<div class="img-fallback">${esc(name)}</div>`;

// запрос к серверу, токен берем из localStorage
async function api(path, method = 'GET', body) {
  const headers = {};
  const token = localStorage.getItem('token');
  if (token) headers.Authorization = 'Bearer ' + token;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch('/api' + path, { method, headers, body: body !== undefined ? JSON.stringify(body) : undefined });
  let data = {};
  try { data = await res.json(); } catch { }
  if (!res.ok) {
    // если токен протух, выходим из аккаунта
    if (res.status === 401 && token) {
      localStorage.removeItem('token');
      S.user = null;
      renderNav();
    }
    throw new Error(data.error || 'Что-то пошло не так');
  }
  return data;
}

// уведомление в углу экрана
function toast(msg, type = '') {
  const t = document.createElement('div');
  t.className = 'toast ' + type;
  t.textContent = msg;
  $('#toasts').appendChild(t);
  setTimeout(() => t.remove(), 3500);
}

// модальное окно
function openModal(html, cls = '') {``
  const m = $('#modal');
  m.innerHTML = `<div class="dialog ${cls}">${html}</div>`;
  m.classList.remove('hidden');
  m.onmousedown = e => { if (e.target === m) closeModal(); };
  m.querySelectorAll('[data-close]').forEach(b => b.addEventListener('click', closeModal));
  return m;
}
function closeModal() {
  const m = $('#modal');
  m.classList.add('hidden');
  m.innerHTML = '';
}
document.addEventListener('keydown', e => { if (e.key === 'Escape') closeModal(); });

// обертка чтобы ошибки показывались уведомлением
function guard(fn) {
  return async e => {
    try { await fn(e); } catch (err) { toast(err.message, 'err'); }
  };
}

// корзина хранится в браузере
function saveCart() {
  localStorage.setItem('cart', JSON.stringify(S.cart));
  updateBadge();
}
const cartCount = () => S.cart.reduce((a, i) => a + i.qty, 0);
function updateBadge(bump) {
  const b = $('#badge');
  if (!b) return;
  b.textContent = cartCount();
  b.style.display = cartCount() ? 'flex' : 'none';
  if (bump) { b.classList.remove('bump'); void b.offsetWidth; b.classList.add('bump'); }
}

// добавляем товар в корзину и запускаем анимацию
function addToCart(p, btn) {
  const found = S.cart.find(i => i.id === p.id);
  if (found) { if (found.qty >= 100) { toast('Максимум 100 штук одного товара', 'warn'); return; } found.qty++; }
  else S.cart.push({ id: p.id, name: p.name, price: p.price, img: p.image_url, cat: p.category_name, qty: 1, stickers: [] });
  saveCart();
  flyToCart(btn);
  toast('«' + p.name + '» в корзине');
}

// летящая точка от кнопки к значку корзины
function flyToCart(btn) {
  const target = $('#cartLink');
  if (!btn || !target) { updateBadge(true); return; }
  const a = btn.getBoundingClientRect();
  const b = target.getBoundingClientRect();
  const dot = document.createElement('div');
  dot.className = 'fly';
  dot.style.left = (a.left + a.width / 2 - 11) + 'px';
  dot.style.top = (a.top + a.height / 2 - 11) + 'px';
  document.body.appendChild(dot);
  const dx = b.left + b.width / 2 - (a.left + a.width / 2);
  const dy = b.top + b.height / 2 - (a.top + a.height / 2);
  const anim = dot.animate([
    { transform: 'translate(0,0) scale(1)', opacity: 1 },
    { transform: `translate(${dx * 0.5}px, ${dy * 0.5 - 60}px) scale(1.2)`, opacity: 1, offset: 0.5 },
    { transform: `translate(${dx}px, ${dy}px) scale(0.3)`, opacity: 0.4 }
  ], { duration: 750, easing: 'ease-in-out' });
  anim.onfinish = () => { dot.remove(); updateBadge(true); };
}

// верхнее меню
function renderNav() {
  const cur = location.hash.replace(/^#\/?/, '').split('/')[0];
  const items = [['', 'Меню'], ['blog', 'Блог']];
  if (S.user && S.user.role === 'admin') items.push(['admin', 'Админка']);
  if (S.user) items.push(['profile', 'Профиль']);
  let h = items.map(([k, t]) => `<a class="nl${cur === k || (k === 'blog' && cur === 'post') ? ' on' : ''}" href="#/${k}">${t}</a>`).join('');
  h += `<a class="nl${cur === 'cart' ? ' on' : ''}" id="cartLink" href="#/cart">Корзина<span class="badge" id="badge"></span></a>`;
  if (S.user) h += `<span class="who">${esc(S.user.name)}</span><button class="btn sm" id="logout">Выйти</button>`;
  else h += `<a class="btn sm primary" href="#/login">Войти</a>`;
  $('#nav').innerHTML = h;
  updateBadge();
  const lo = $('#logout');
  if (lo) lo.onclick = guard(async () => {
    try { await api('/logout', 'POST'); } catch { }
    localStorage.removeItem('token');
    S.user = null;
    location.hash = '#/';
    route();
  });
}

// страницы сайта и права доступа к ним
const routes = { '': home, cart, login: authPage, profile, blog, post, admin };
const access = { profile: ['client', 'admin'], admin: ['admin'] };

async function route() {
  closeModal();
  const parts = location.hash.replace(/^#\/?/, '').split('/');
  const key = parts[0];
  const fn = routes[key] || home;
  const need = access[key];
  if (need && !S.user) { toast('Сначала войдите в аккаунт', 'warn'); location.hash = '#/login'; return; }
  if (need && !need.includes(S.user.role)) { toast('Эта страница только для администратора', 'err'); location.hash = '#/'; return; }
  renderNav();
  view.innerHTML = '';
  view.onclick = null;
  view.oninput = null;
  view.onchange = null;
  window.scrollTo(0, 0);
  try { await fn(parts[1]); } catch (err) { view.innerHTML = `<div class="empty">${esc(err.message)}</div>`; }
}

// главная страница: приветствие и каталог
let F = { q: '', cat: 0 };

async function home() {
  view.innerHTML = `
  <section class="hero">
    <div class="bean b1"></div><div class="bean b2"></div>
    <h1>Кофе, который согревает, и десерты, которые хочется попробовать</h1>
    <p>Свежая обжарка ежедневно, домашние десерты и теплая выпечка. Заказывайте онлайн и забирайте без очереди.</p>
    <a class="btn primary" href="#menu" id="toMenu">Смотреть меню</a>
  </section>
  <h2 id="menu">Меню</h2>
  <div class="bar">
    <input id="fq" type="search" placeholder="Найти напиток или десерт" value="${esc(F.q)}">
    <div class="chips" id="chips"></div>
  </div>
  <div class="grid" id="grid"></div>`;
  $('#toMenu').onclick = e => { e.preventDefault(); $('#menu').scrollIntoView({ behavior: 'smooth' }); };
  S.products = await api('/products');
  drawChips();
  drawGrid();
  $('#fq').oninput = e => { F.q = e.target.value; drawGrid(); };
  $('#grid').onclick = e => {
    const b = e.target.closest('[data-add]');
    if (!b) return;
    addToCart(S.products.find(p => p.id === Number(b.dataset.add)), b);
  };
}

function drawChips() {
  const all = [{ id: 0, name: 'Все' }].concat(S.cats);
  const box = $('#chips');
  box.innerHTML = all.map(c => `<button class="chip${F.cat === c.id ? ' on' : ''}" data-c="${c.id}">${esc(c.name)}</button>`).join('');
  box.onclick = e => {
    const b = e.target.closest('[data-c]');
    if (!b) return;
    F.cat = Number(b.dataset.c);
    drawChips();
    drawGrid();
  };
}

function drawGrid() {
  const q = F.q.trim().toLowerCase();
  const list = S.products.filter(p => (!F.cat || p.category_id === F.cat) && (!q || p.name.toLowerCase().includes(q) || p.description.toLowerCase().includes(q)));
  $('#grid').innerHTML = list.length ? list.map(p => `
    <article class="card">
      <div class="pic">${picHtml(p.image_url, p.name)}<span class="tag">${esc(p.category_name)}</span></div>
      <div class="cbody">
        <h3>${esc(p.name)}</h3>
        <p>${esc(p.description)}</p>
        <div class="cfoot"><span class="price">${money(p.price)}</span><button class="btn primary sm" data-add="${p.id}">В корзину</button></div>
      </div>
    </article>`).join('') : '<div class="empty" style="grid-column:1/-1">Ничего не нашли, попробуйте другой запрос</div>';
}

// корзина: товары, стикеры и оформление заказа
let lastSticker = '';

async function cart() {
  if (!S.cart.length) {
    view.innerHTML = `<h2>Корзина</h2><div class="empty">В корзине пока пусто.<br><br><a class="btn primary" href="#/">Выбрать что-нибудь вкусное</a></div>`;
    return;
  }
  // сначала обновляем цены с сервера
  await refreshCart();
  if (!S.cart.length) { cart(); return; }
  const co = S.checkout || (S.checkout = { delivery: 'pickup', payment: 'card', address: '', comment: '' });
  view.innerHTML = `
  <h2>Корзина</h2>
  <div class="cartgrid">
    <div>
      <p class="hint" style="margin-top:-8px">Перетащите стикер на нужный товар (например, «С собой» или «Подарок»)</p>
      <div class="lines" id="lines"></div>
    </div>
    <div class="side">
      <div class="box">
        <h3>Стикеры</h3>
        <div class="palette" id="palette">${S.stickers.map(s => `<span class="sticker" draggable="true" data-st="${esc(s)}">${esc(s)}</span>`).join('')}</div>
        <p class="hint" style="margin:10px 0 0">Перетащите стикер на карточку товара</p>
      </div>
      <div class="box">
        <h3>Оформление</h3>
        <div class="form">
          <div class="opts">
            <label class="opt"><input type="radio" name="delivery" value="pickup" ${co.delivery === 'pickup' ? 'checked' : ''}> Самовывоз</label>
            <label class="opt"><input type="radio" name="delivery" value="delivery" ${co.delivery === 'delivery' ? 'checked' : ''}> Доставка</label>
          </div>
          <label id="addrBox" style="${co.delivery === 'delivery' ? '' : 'display:none'}">Адрес доставки<input id="addr" maxlength="200" value="${esc(co.address)}" placeholder="Улица, дом, квартира"></label>
          <label>Способ оплаты
            <select id="pay">${Object.entries(PAY).map(([k, v]) => `<option value="${k}" ${co.payment === k ? 'selected' : ''}>${v}</option>`).join('')}</select></label>
          <label>Комментарий<textarea id="com" maxlength="300" placeholder="Например, без корицы">${esc(co.comment)}</textarea></label>
        </div>
        <div class="total"><span>Итого</span><span id="total"></span></div>
        <div class="acts" style="justify-content:stretch"><button class="btn primary" style="flex:1" id="order">${S.user ? 'Оформить заказ' : 'Войти и оформить'}</button></div>
      </div>
    </div>
  </div>`;
  drawLines();

  // перетаскивание стикеров
  $('#palette').addEventListener('dragstart', e => {
    const s = e.target.closest('[data-st]');
    if (!s) return;
    e.dataTransfer.setData('text/plain', s.dataset.st);
    e.dataTransfer.effectAllowed = 'copy';
  });
  const lines = $('#lines');
  lines.addEventListener('dragover', e => {
    const l = e.target.closest('.line');
    if (!l) return;
    e.preventDefault();
    lines.querySelectorAll('.over').forEach(x => { if (x !== l) x.classList.remove('over'); });
    l.classList.add('over');
  });
  lines.addEventListener('dragleave', e => {
    const l = e.target.closest('.line');
    if (l && !l.contains(e.relatedTarget)) l.classList.remove('over');
  });
  lines.addEventListener('drop', e => {
    const l = e.target.closest('.line');
    if (!l) return;
    e.preventDefault();
    l.classList.remove('over');
    addSticker(Number(l.dataset.id), e.dataTransfer.getData('text/plain'));
  });

  // кнопки в карточках корзины
  lines.addEventListener('click', e => {
    const b = e.target.closest('button');
    if (!b) return;
    const id = Number(b.dataset.id);
    const item = S.cart.find(i => i.id === id);
    if (b.dataset.inc && item && item.qty < 20) item.qty++;
    if (b.dataset.dec && item && item.qty > 1) item.qty--;
    if (b.dataset.del) S.cart = S.cart.filter(i => i.id !== id);
    if (b.dataset.rm && item) item.stickers = item.stickers.filter(x => x !== b.dataset.rm);
    saveCart();
    if (!S.cart.length) { cart(); return; }
    drawLines();
  });
  // запасной вариант для телефона: выбор стикера из списка
  lines.addEventListener('change', e => {
    const sel = e.target.closest('select[data-add]');
    if (!sel || !sel.value) return;
    addSticker(Number(sel.dataset.add), sel.value);
  });

  // запоминаем что ввел человек в форме
  view.oninput = e => {
    if (e.target.id === 'addr') co.address = e.target.value;
    if (e.target.id === 'com') co.comment = e.target.value;
  };
  view.onchange = e => {
    if (e.target.name === 'delivery') {
      co.delivery = e.target.value;
      $('#addrBox').style.display = co.delivery === 'delivery' ? '' : 'none';
    }
    if (e.target.id === 'pay') co.payment = e.target.value;
  };
  $('#order').onclick = guard(sendOrder);
}

// обновляем корзину по данным сервера
async function refreshCart() {
  const r = await api('/cart/calc', 'POST', { items: S.cart.map(i => ({ productId: i.id, qty: i.qty, stickers: i.stickers })) });
  let removed = 0;
  S.cart = S.cart.filter(i => {
    const l = r.lines.find(x => x.productId === i.id);
    if (!l || !l.ok) { removed++; return false; }
    i.price = l.price; i.name = l.name; i.img = l.image_url; i.cat = l.category_name;
    return true;
  });
  if (removed) toast('Некоторые товары закончились и убраны из корзины', 'warn');
  saveCart();
}

function addSticker(id, st) {
  const item = S.cart.find(i => i.id === id);
  if (!item || !S.stickers.includes(st)) return;
  if (item.stickers.includes(st)) { toast('Такой стикер уже есть', 'warn'); return; }
  if (item.stickers.length >= 5) { toast('Максимум 5 стикеров на товар', 'warn'); return; }
  item.stickers.push(st);
  lastSticker = id + '|' + st;
  saveCart();
  drawLines();
}

// рисуем карточки товаров в корзине
function drawLines() {
  $('#lines').innerHTML = S.cart.map(i => `
    <div class="line" data-id="${i.id}">
      <div class="pic">${picHtml(i.img, i.name)}</div>
      <div>
        <h3>${esc(i.name)}</h3>
        <div class="muted">${money(i.price)} за штуку</div>
        <div class="qty"><button data-dec="1" data-id="${i.id}">-</button><b>${i.qty}</b><button data-inc="1" data-id="${i.id}">+</button></div>
        <div class="stickers">
          ${i.stickers.map(s => {
    let cls = 'st';
    if (s === 'Срочно') cls += ' urgent';
    else if (s === 'Подарок') cls += ' gift';
    else if (s === 'С собой') cls += ' takeaway';
    else if (s === 'Без сахара') cls += ' nosugar';
    else if (s === 'Побольше сахара') cls += ' foam';
    if (lastSticker === i.id + '|' + s) cls += ' new';
    return `<span class="${cls}">${esc(s)}<button data-rm="${esc(s)}" data-id="${i.id}" title="Убрать">x</button></span>`;
    }).join('')}
          <select data-add="${i.id}" style="width:auto;padding:4px 8px;font-size:13px"><option value="">+ стикер</option>${S.stickers.filter(s => !i.stickers.includes(s)).map(s => `<option>${esc(s)}</option>`).join('')}</select>
        </div>
      </div>
      <div class="lr"><b class="price">${money(i.price * i.qty)}</b><button class="btn danger sm" data-del="1" data-id="${i.id}">Удалить</button></div>
    </div>`).join('');
  lastSticker = '';
  $('#total').textContent = money(S.cart.reduce((a, i) => a + i.price * i.qty, 0));
}

// отправка заказа
async function sendOrder() {
  if (!S.user) { toast('Войдите, чтобы оформить заказ', 'warn'); location.hash = '#/login'; return; }
  const co = S.checkout;
  if (co.delivery === 'delivery' && co.address.trim().length < 5) { toast('Укажите адрес доставки', 'warn'); return; }
  const r = await api('/orders', 'POST', {
    items: S.cart.map(i => ({ productId: i.id, qty: i.qty, stickers: i.stickers })),
    payment: co.payment, delivery: co.delivery, address: co.address, comment: co.comment
  });
  S.cart = [];
  S.checkout = null;
  saveCart();
  await showReceipt(r.id, () => { location.hash = '#/profile'; });
}

// чек заказа
async function showReceipt(id, after) {
  const { order: o, items } = await api('/orders/' + id);
  
  // Формируем список товаров с цветными стикерами
  const rows = items.map(i => {
    let stickerHtml = '';
    if (i.stickers) {
      const stList = i.stickers.split(',');
      stickerHtml = `<div style="margin-bottom: 12px; display: flex; gap: 6px; flex-wrap: wrap;">`;
      for (let j = 0; j < stList.length; j++) {
        const s = stList[j];
        let bg = '#a855f7'; // фиолетовый по умолчанию
        let color = '#fff';
        if (s === 'Срочно') { bg = '#ef4444'; }
        else if (s === 'Подарок') { bg = '#f472b6'; }
        else if (s === 'С собой') { bg = '#fb923c'; }
        else if (s === 'Без сахара') { bg = '#34d399'; color = '#000'; }
        
        stickerHtml += `<span style="background: ${bg}; color: ${color}; font-size: 12px; font-weight: 700; padding: 4px 10px; border-radius: 6px; text-transform: uppercase;">${esc(s)}</span>`;
      }
      stickerHtml += `</div>`;
    }

    return `
      <div class="ri" style="display: flex; justify-content: space-between; margin-bottom: 8px;">
        <span>${esc(i.name)} x${i.qty}</span>
        <span>${money(i.price * i.qty)}</span>
      </div>
      ${stickerHtml}
    `;
  }).join('');

  // Само окно чека
  const m = openModal(`
    <div class="receipt" style="background: var(--paper); border: 1px solid var(--line); border-radius: 12px; max-width: 400px; margin: 0 auto; padding: 20px;">
      <h3 style="text-align: center; color: var(--green); margin-bottom: 5px;">Заказ №${String(o.id).padStart(4, '0')}</h3>
      <div class="c" style="text-align: center; color: var(--muted); font-size: 13px; margin-bottom: 15px;">Кофейня «Кремка»</div>
      
      <div class="ri" style="display: flex; justify-content: space-between; margin-bottom: 8px;"><span>Дата:</span><span>${fmtDate(o.created, true)}</span></div>
      <div class="ri" style="display: flex; justify-content: space-between; margin-bottom: 8px;"><span>Клиент:</span><span>${esc(o.user_name)}</span></div>
      <div class="ri" style="display: flex; justify-content: space-between; margin-bottom: 8px;"><span>Оплата:</span><span>${PAY[o.payment]}</span></div>
      <div class="ri" style="display: flex; justify-content: space-between; margin-bottom: 8px;"><span>Получение:</span><span>${DEL[o.delivery]}</span></div>
      ${o.delivery === 'delivery' ? `<div class="ri" style="display: flex; justify-content: space-between; margin-bottom: 8px;"><span>Адрес:</span><span>${esc(o.address)}</span></div>` : ''}
      ${o.comment ? `<div class="ri" style="display: flex; justify-content: space-between; margin-bottom: 8px;"><span>Комментарий:</span><span>${esc(o.comment)}</span></div>` : ''}
      
      <hr style="border: none; border-top: 1px solid var(--line); margin: 15px 0;">
      
      ${rows}
      
      <hr style="border: none; border-top: 1px solid var(--line); margin: 15px 0;">
      
      <div class="rt" style="display: flex; justify-content: space-between; font-size: 20px; font-weight: 700; color: var(--green);"><span>Итого:</span><span>${money(o.total)}</span></div>
    </div>
    <div class="acts" style="justify-content:center; margin-top: 15px;">
      <button class="btn dark" id="print">Печать</button>
      <button class="btn" data-close>Закрыть</button>
    </div>`);
    
  $('#print', m).onclick = () => window.print();
  m.querySelectorAll('[data-close]').forEach(b => b.addEventListener('click', () => { if (after) after(); }));
  m.onmousedown = e => { if (e.target === m) { closeModal(); if (after) after(); } };
}

// вход и регистрация
let authMode = 'login';
function authPage() {
  if (S.user) { location.hash = '#/'; return; }
  const reg = authMode === 'register';
  view.innerHTML = `
  <div class="box authbox">
    <div class="tabs">
      <button class="tab${reg ? '' : ' on'}" data-m="login">Вход</button>
      <button class="tab${reg ? ' on' : ''}" data-m="register">Регистрация</button>
    </div>
    <form class="form" id="af" novalidate>
      ${reg ? '<label>Имя<input name="name" autocomplete="name" placeholder="Как вас зовут"></label>' : ''}
      <label>Email<input name="email" type="email" autocomplete="email" placeholder="you@example.com"></label>
      <label>Пароль<input name="password" type="password" autocomplete="${reg ? 'new-password' : 'current-password'}" placeholder="Минимум 6 символов"></label>
      <button class="btn primary">${reg ? 'Создать аккаунт' : 'Войти'}</button>
    </form>
  </div>`;
  view.querySelectorAll('[data-m]').forEach(b => b.onclick = () => { authMode = b.dataset.m; authPage(); });
  $('#af').onsubmit = guard(async ev => {
    ev.preventDefault();
    const f = ev.target.elements;
    const email = f.email.value.trim();
    if (!email || !f.password.value) { toast('Заполните email и пароль', 'warn'); return; }
    if (reg && f.name.value.trim().length < 2) { toast('Введите имя, минимум 2 буквы', 'warn'); return; }
    if (reg && f.password.value.length < 6) { toast('Пароль минимум 6 символов', 'warn'); return; }
    const body = { email, password: f.password.value };
    if (reg) body.name = f.name.value.trim();
    const r = await api(reg ? '/register' : '/login', 'POST', body);
    localStorage.setItem('token', r.token);
    S.user = r.user;
    toast('Добро пожаловать, ' + r.user.name );
    location.hash = S.cart.length ? '#/cart' : '#/';
  });
}

// профиль и мои заказы
async function profile() {
  const orders = await api('/orders/my');
  view.innerHTML = `
    <h2>Профиль</h2>
    <p class="muted" style="margin-top:-8px">${esc(S.user.name)}, ${esc(S.user.email)}</p>
    <h3>Мои заказы</h3>
    ${orders.length ? `<div class="tw"><table><thead><tr><th>№</th><th>Дата</th><th>Сумма</th><th>Оплата</th><th>Получение</th><th>Статус</th><th></th></tr></thead><tbody>
    ${orders.map(o => `<tr><td>${String(o.id).padStart(4, '0')}</td><td>${fmtDate(o.created, true)}</td><td>${money(o.total)}</td><td>${PAY[o.payment]}</td><td>${DEL[o.delivery]}</td>
      <td><span class="status s-${o.status}">${STATUS[o.status]}</span></td><td><button class="btn sm" data-receipt="${o.id}">Чек</button></td></tr>`).join('')}</tbody></table></div>` : '<div class="empty">Заказов пока нет. <a href="#/">Перейти в меню</a></div>'}`;
  view.onclick = guard(async e => {
    const b = e.target.closest('[data-receipt]');
    if (b) await showReceipt(b.dataset.receipt);
  });
}

// блог
async function blog() {
  const posts = await api('/posts');
  view.innerHTML = `<h2>Блог о кофе</h2>
    <div class="grid">${posts.map(p => `
      <a class="card" href="#/post/${p.id}" style="text-decoration:none;color:inherit">
        <div class="pic post-img">${picHtml(p.image_url, p.title)}</div>
        <div class="cbody"><span class="muted" style="font-size:13px">${fmtDate(p.created)}</span><h3>${esc(p.title)}</h3><p>${esc(p.summary)}</p></div>
      </a>`).join('')}</div>`;
}

async function post(id) {
  const p = await api('/posts/' + Number(id));
  view.innerHTML = `
  <div class="postpage">
    <a href="#/blog">К списку статей</a>
    <h2 style="margin-top:12px">${esc(p.title)}</h2>
    <span class="muted">${fmtDate(p.created)}</span>
    <div class="pic">${picHtml(p.image_url, p.title)}</div>
    ${p.body.split('\n\n').map(t => `<p>${esc(t)}</p>`).join('')}
  </div>`;
}

// админка
let aTab = 'products';
async function admin() {
  const tab = (k, t) => `<button class="tab${aTab === k ? ' on' : ''}" data-t="${k}">${t}</button>`;
  view.innerHTML = `<h2>Админ-панель</h2>
    <div class="tabs">${tab('products', 'Товары')}${tab('posts', 'Блог')}${tab('orders', 'Заказы')}${tab('stats', 'Статистика')}</div>
    <div id="pane"></div>`;
  view.onclick = e => {
    const t = e.target.closest('[data-t]');
    if (t) { aTab = t.dataset.t; admin(); }
  };
  if (aTab === 'products') await adminProducts();
  else if (aTab === 'posts') await adminPosts();
  else if (aTab === 'orders') await adminOrders();
  else await adminStats();
}

// товары
async function adminProducts() {
  const list = await api('/admin/products');
  $('#pane').innerHTML = `
    <div class="bartop"><h3 style="margin:0">Товары (${list.length})</h3><button class="btn primary" id="addp">Добавить товар</button></div>
    <div class="tw"><table><thead><tr><th>Название</th><th>Категория</th><th>Цена</th><th>В меню</th><th></th></tr></thead><tbody>
    ${list.map(p => `<tr><td>${esc(p.name)}</td><td>${esc(p.category_name)}</td><td>${money(p.price)}</td><td>${p.available ? 'да' : 'скрыт'}</td>
      <td><button class="btn sm" data-edit="${p.id}">Изменить</button><button class="btn sm danger" data-del="${p.id}">Удалить</button></td></tr>`).join('')}</tbody></table></div>`;
  $('#addp').onclick = () => productModal(null);
  $('#pane').onclick = guard(async e => {
    const ed = e.target.closest('[data-edit]');
    const dl = e.target.closest('[data-del]');
    if (ed) productModal(list.find(x => x.id === Number(ed.dataset.edit)));
    if (dl) {
      if (!confirm('Удалить товар?')) return;
      await api('/admin/products/' + dl.dataset.del, 'DELETE');
      toast('Товар удален');
      admin();
    }
  });
}

function productModal(p) {
  const v = p || { name: '', category_id: S.cats[0].id, description: '', image_url: '', price: '', available: 1 };
  const m = openModal(`
    <form id="pf" novalidate>
      <h3>${p ? 'Изменить товар' : 'Новый товар'}</h3>
      <div class="form">
        <label>Название<input name="name" maxlength="80" value="${esc(v.name)}"></label>
        <div class="row2">
          <label>Категория<select name="cat">${S.cats.map(c => `<option value="${c.id}" ${c.id === v.category_id ? 'selected' : ''}>${esc(c.name)}</option>`).join('')}</select></label>
          <label>Цена, ₽<input type="number" name="price" min="1" step="1" value="${v.price}"></label>
        </div>
        <label>Описание<textarea name="desc" maxlength="400">${esc(v.description)}</textarea></label>
        <label>Ссылка на картинку (из интернета)<input name="img" placeholder="https://..." value="${esc(v.image_url)}"></label>
        <label style="display:flex;gap:8px;align-items:center;color:var(--text)"><input type="checkbox" name="av" ${v.available ? 'checked' : ''}> Показывать в меню</label>
      </div>
      <div class="acts"><button type="button" class="btn" data-close>Отмена</button><button class="btn primary">Сохранить</button></div>
    </form>`);
  $('#pf', m).onsubmit = guard(async ev => {
    ev.preventDefault();
    const f = ev.target.elements;
    const price = Number(f.price.value);
    if (f.name.value.trim().length < 2) { toast('Название минимум 2 символа', 'warn'); return; }
    if (!(price > 0)) { toast('Цена должна быть больше нуля', 'warn'); return; }
    const body = { name: f.name.value, categoryId: Number(f.cat.value), description: f.desc.value, imageUrl: f.img.value, price, available: f.av.checked ? 1 : 0 };
    await api(p ? '/admin/products/' + p.id : '/admin/products', p ? 'PUT' : 'POST', body);
    closeModal();
    toast('Сохранено');
    admin();
  });
}

// статьи блога
async function adminPosts() {
  const list = await api('/posts');
  $('#pane').innerHTML = `
    <div class="bartop"><h3 style="margin:0">Статьи (${list.length})</h3><button class="btn primary" id="addpost">Новая статья</button></div>
    <div class="tw"><table><thead><tr><th>Заголовок</th><th>Дата</th><th></th></tr></thead><tbody>
    ${list.map(p => `<tr><td>${esc(p.title)}</td><td>${fmtDate(p.created)}</td>
      <td><button class="btn sm" data-edit="${p.id}">Изменить</button><button class="btn sm danger" data-del="${p.id}">Удалить</button></td></tr>`).join('')}</tbody></table></div>`;
  $('#addpost').onclick = () => postModal(null);
  $('#pane').onclick = guard(async e => {
    const ed = e.target.closest('[data-edit]');
    const dl = e.target.closest('[data-del]');
    if (ed) postModal(await api('/posts/' + ed.dataset.edit));
    if (dl) {
      if (!confirm('Удалить статью?')) return;
      await api('/admin/posts/' + dl.dataset.del, 'DELETE');
      toast('Статья удалена');
      admin();
    }
  });
}

function postModal(p) {
  const v = p || { title: '', summary: '', body: '', image_url: '' };
  const m = openModal(`
    <form id="qf" novalidate>
      <h3>${p ? 'Изменить статью' : 'Новая статья'}</h3>
      <div class="form">
        <label>Заголовок<input name="title" maxlength="120" value="${esc(v.title)}"></label>
        <label>Краткое описание<input name="sum" maxlength="300" value="${esc(v.summary)}"></label>
        <label>Ссылка на картинку<input name="img" placeholder="https://..." value="${esc(v.image_url)}"></label>
        <label>Текст (абзацы отделяйте пустой строкой)<textarea name="body" style="min-height:180px">${esc(v.body)}</textarea></label>
      </div>
      <div class="acts"><button type="button" class="btn" data-close>Отмена</button><button class="btn primary">Сохранить</button></div>
    </form>`);
  $('#qf', m).onsubmit = guard(async ev => {
    ev.preventDefault();
    const f = ev.target.elements;
    if (f.title.value.trim().length < 3) { toast('Заголовок минимум 3 символа', 'warn'); return; }
    if (f.sum.value.trim().length < 5) { toast('Напишите краткое описание', 'warn'); return; }
    if (f.body.value.trim().length < 20) { toast('Текст статьи слишком короткий', 'warn'); return; }
    const body = { title: f.title.value, summary: f.sum.value, body: f.body.value, imageUrl: f.img.value };
    await api(p ? '/admin/posts/' + p.id : '/admin/posts', p ? 'PUT' : 'POST', body);
    closeModal();
    toast('Сохранено');
    admin();
  });
}

// заказы
async function adminOrders() {
  const list = await api('/admin/orders');
  $('#pane').innerHTML = `
    <div class="bartop"><h3 style="margin:0">Заказы (${list.length})</h3></div>
    ${list.length ? `<div class="tw"><table><thead><tr><th>№</th><th>Дата</th><th>Клиент</th><th>Сумма</th><th>Получение</th><th>Статус</th><th></th></tr></thead><tbody>
    ${list.map(o => `<tr><td>${String(o.id).padStart(4, '0')}</td><td>${fmtDate(o.created, true)}</td><td>${esc(o.user_name)}</td><td>${money(o.total)}</td><td>${DEL[o.delivery]}</td>
      <td><select data-status="${o.id}" style="width:150px;padding:6px 8px">${Object.entries(STATUS).map(([k, v]) => `<option value="${k}" ${o.status === k ? 'selected' : ''}>${v}</option>`).join('')}</select></td>
      <td><button class="btn sm" data-receipt="${o.id}">Чек</button></td></tr>`).join('')}</tbody></table></div>` : '<div class="empty">Заказов пока нет</div>'}`;
  $('#pane').onclick = guard(async e => {
    const b = e.target.closest('[data-receipt]');
    if (b) await showReceipt(b.dataset.receipt);
  });
  $('#pane').onchange = guard(async e => {
    const s = e.target.closest('[data-status]');
    if (!s) return;
    await api(`/admin/orders/${s.dataset.status}/status`, 'PUT', { status: s.value });
    toast('Статус обновлен');
  });
}

// статистика и графики
const COLORS = ['#c4813f', '#5a3a28', '#e8c08a', '#5f8a5b', '#b5483a'];

async function adminStats() {
  const s = await api('/admin/stats');
  $('#pane').innerHTML = `
    <div class="kpis">
      <div class="kpi"><span>Выручка</span><b>${money(s.revenue)}</b></div>
      <div class="kpi"><span>Заказов всего</span><b>${s.orders}</b></div>
      <div class="kpi"><span>Заказов сегодня</span><b>${s.today}</b></div>
      <div class="kpi"><span>Средний чек</span><b>${money(s.avg)}</b></div>
      <div class="kpi"><span>Клиентов</span><b>${s.clients}</b></div>
    </div>
    <div class="charts">
      <div class="box chart"><h3>Выручка за 14 дней</h3>${barChart(s.days)}</div>
      <div class="box chart"><h3>Продажи по категориям</h3>${donut(s.cats)}</div>
      <div class="box chart"><h3>Популярные товары</h3>${hbars(s.top)}</div>
      <div class="box chart"><h3>Любимые стикеры</h3>${hbars(s.stickers)}</div>
    </div>
    <h3>Последние заказы</h3>
    <div class="tw"><table><thead><tr><th>№</th><th>Дата</th><th>Клиент</th><th>Сумма</th><th>Статус</th></tr></thead><tbody>
    ${s.recent.map(o => `<tr><td>${String(o.id).padStart(4, '0')}</td><td>${fmtDate(o.created, true)}</td><td>${esc(o.user_name)}</td><td>${money(o.total)}</td><td><span class="status s-${o.status}">${STATUS[o.status]}</span></td></tr>`).join('')}</tbody></table></div>`;
}

// столбчатая диаграмма на чистом SVG
function barChart(days) {
  const W = 560, H = 250, pl = 46, pb = 30, pt = 12;
  const max = Math.max(...days.map(d => d.sum), 1);
  const step = Math.pow(10, Math.floor(Math.log10(max)));
  const top = Math.ceil(max / step) * step;
  const bw = (W - pl - 8) / days.length;
  let g = '';
  for (let i = 0; i <= 4; i++) {
    const y = pt + (H - pt - pb) * (1 - i / 4);
    g += `<line x1="${pl}" x2="${W - 6}" y1="${y}" y2="${y}" stroke="#e2cfb2"/><text x="${pl - 6}" y="${y + 4}" text-anchor="end">${Math.round(top * i / 4)}</text>`;
  }
  const bars = days.map((d, i) => {
    const h = (H - pt - pb) * (d.sum / top);
    const x = pl + i * bw + bw * 0.15;
    const dd = d.day.slice(8) + '.' + d.day.slice(5, 7);
    return `<rect x="${x}" y="${H - pb - h}" width="${bw * 0.7}" height="${h}" rx="4" fill="url(#bgc)"><title>${dd}: ${money(d.sum)}, заказов: ${d.cnt}</title></rect>
      ${i % 2 === 0 ? `<text x="${x + bw * 0.35}" y="${H - 12}" text-anchor="middle">${dd}</text>` : ''}`;
  }).join('');
  return `<svg viewBox="0 0 ${W} ${H}"><defs><linearGradient id="bgc" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#c4813f"/><stop offset="1" stop-color="#5a3a28"/></linearGradient></defs>${g}${bars}</svg>`;
}

// круговая диаграмма
function donut(cats) {
  const total = cats.reduce((a, c) => a + c.sum, 0);
  if (!total) return '<div class="empty">Пока нет данных</div>';
  const R = 62, C = 2 * Math.PI * R;
  let off = 0;
  const segs = cats.map((c, i) => {
    const len = C * c.sum / total;
    const s = `<circle cx="100" cy="100" r="${R}" fill="none" stroke="${COLORS[i % COLORS.length]}" stroke-width="30" stroke-dasharray="${len} ${C - len}" stroke-dashoffset="${-off}" transform="rotate(-90 100 100)"><title>${esc(c.name)}: ${money(c.sum)}</title></circle>`;
    off += len;
    return s;
  }).join('');
  const legend = cats.map((c, i) => `<span><i style="background:${COLORS[i % COLORS.length]}"></i>${esc(c.name)}: ${money(c.sum)} (${Math.round(c.sum * 100 / total)}%)</span>`).join('');
  return `<svg viewBox="0 0 200 200" style="max-width:230px;margin:0 auto">${segs}<text x="100" y="98" text-anchor="middle" style="font-size:20px;font-weight:700;fill:#ffffff">${Math.round(total / 1000)}к</text><text x="100" y="116" text-anchor="middle" style="fill:#94a3b8">рублей</text></svg><div class="legend">${legend}</div>`;
}

// горизонтальные полоски
function hbars(list) {
  if (!list.length) return '<div class="empty">Пока нет данных</div>';
  const max = Math.max(...list.map(t => t.cnt), 1);
  return `<div class="hb">${list.map(t => `<div class="r"><span>${esc(t.name)}</span><div class="tr"><i style="width:${t.cnt / max * 100}%"></i></div><b>${t.cnt}</b></div>`).join('')}</div>`;
}

// запуск сайта
window.addEventListener('hashchange', route);
(async function init() {
  try {
    if (localStorage.getItem('token')) {
      try { S.user = await api('/me'); } catch { S.user = null; }
    }
    S.cats = await api('/categories');
    S.stickers = await api('/stickers');
  } catch (err) {
    toast(err.message, 'err');
  }
  route();
})();
