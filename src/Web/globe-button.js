// Globe: a globe button in the header; a click covers the page with a layer in the globe's colour that shows the globe.
// The layer has only an X; with two or more globes a small tab below it slides in a side menu to switch globes.
// X, Esc and Back close it (Esc closes an open menu first). A double-click does what the admin set for the title's media
// type (detail page, play, trailer, photo viewer, a game's start). While Jellyfin shows the player or a page the layer is hidden, and it
// comes back with the same view when Jellyfin returns to where it was opened.
(() => {
  if (window.globeButtonLoaded) return;
  window.globeButtonLoaded = true;

  const texts = {
    en: { noGlobe: 'No globe has been created yet.', adminHint: 'Create one under Dashboard › Plugins › Globe.',
      notReady: 'This globe is not built yet.', failed: 'This could not be started.', started: 'Started.', unsupported: 'This device cannot show the globe.', building: 'being built', close: 'Close', globes: 'Globes', title: 'Globe' },
    de: { noGlobe: 'Es wurde noch kein Globe erstellt.', adminHint: 'Erstelle einen unter Dashboard › Plugins › Globe.',
      notReady: 'Dieser Globe ist noch nicht gebaut.', failed: 'Das ließ sich nicht starten.', started: 'Gestartet.', unsupported: 'Dieses Gerät kann den Globe nicht anzeigen.', building: 'wird gebaut', close: 'Schließen', globes: 'Globes', title: 'Globe' },
  };
  const text = key => (texts[(document.documentElement.lang || navigator.language || 'en').slice(0, 2).toLowerCase()] || texts.en)[key];

  // One path (ring, meridian, two parallels): header icons are drawn at 80 % white, so separate shapes would look
  // brighter where they overlap. Same 960 grid and stroke as Jellyfin's Material icons.
  const globeIcon = '<span class="material-icons" aria-hidden="true"><svg xmlns="http://www.w3.org/2000/svg" width="1em" height="1em"'
    + ' viewBox="0 -960 960 960" fill="none" stroke="currentColor" stroke-width="80" style="display:block">'
    + '<path d="M120 -480A360 360 0 1 0 840 -480A360 360 0 1 0 120 -480ZM330 -480A150 360 0 1 0 630 -480A150 360 0 1 0 330 -480ZM141 -600H819M141 -360H819"/>'
    + '</svg></span>';
  const closeIcon = '<svg viewBox="0 0 24 24" width="32" height="32" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">'
    + '<path d="M6 6l12 12M18 6L6 18"/></svg>';
  const tabIcon = '<svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">'
    + '<path d="M15 6l-6 6 6 6"/></svg>';

  let config = null;       // ClientConfiguration of the signed-in user
  let configUser = null;
  let layer = null, globe = null, menu = null, current = null;
  let away = null;         // hash of the page the globe was opened on, while Jellyfin shows the player or a page
  let opening = 0;         // counts openings, so a late answer for a closed or switched globe is dropped

  const api = () => window.ApiClient;
  const lastUsedKey = () => 'globe-last-' + api().getCurrentUserId();

  async function loadConfig() {
    configUser = api().getCurrentUserId();
    config = await api().getJSON(api().getUrl('Globe/ClientConfiguration'));
    return config;
  }

  function close(fromLayer) {
    opening++;
    globe?.dispose();
    globe = null;
    layer?.remove();
    layer = menu = current = null;
    away = null;
    animation = animatedTiles = null;
    clearInterval(menuTimer);
    removeEventListener('keydown', onKey);
    // open() added a history entry so that Back closes the globe; X and Esc take it away again.
    if (fromLayer && history.state?.globe) history.back();
  }

  function onKey(e) {
    if (e.key !== 'Escape' || !layer || away !== null) return;
    if (menu?.classList.contains('globeMenuOpen')) toggleMenu(false);
    else close(true);
  }

  function hide() {
    away = location.hash;
    globe?.pause();
    layer.style.display = 'none';
  }

  addEventListener('popstate', () => {
    if (layer && away === null && !history.state?.globe) close(false);
    // Jellyfin's photo viewer is a dialog that keeps the address; closing it goes back in history, and the globe returns
    // once the viewer has faded out (checked for up to two seconds).
    else if (layer && away !== null && location.hash === away) {
      let tries = 10;
      const check = () => {
        if (away === null || location.hash !== away) return;
        if (!document.querySelector('.slideshowDialog')) resume();
        else if (--tries > 0) setTimeout(check, 200);
      };
      setTimeout(check, 200);
    }
  });

  function resume() {
    away = null;
    layer.style.display = '';
    globe?.resume();
  }

  addEventListener('hashchange', () => {
    if (away !== null && layer && location.hash === away) resume();
  });

  // A short line at the bottom of the layer; Jellyfin's own messages would sit behind the globe.
  function note(line) {
    layer?.querySelector('.globeNote')?.remove();
    const box = Object.assign(document.createElement('div'), { className: 'globeNote', textContent: line });
    box.style.cssText = 'position:absolute;left:50%;bottom:32px;transform:translateX(-50%);z-index:5;padding:8px 16px;border-radius:4px;'
      + 'background:rgba(0,0,0,.75);color:#eee;font:14px system-ui,sans-serif;pointer-events:none;';
    layer?.appendChild(box);
    setTimeout(() => box.remove(), 3000);
  }

  // Playing goes through Jellyfin's own session, so its player (or photo viewer) opens; the layer waits hidden.
  async function playNow(itemIds) {
    const sessions = await api().getJSON(api().getUrl('Sessions', { deviceId: api().deviceId() }));
    if (!sessions.length) return note(text('failed'));
    hide();
    try {
      await api().ajax({ type: 'POST', url: api().getUrl(`Sessions/${sessions[0].Id}/Playing`, { playCommand: 'PlayNow', itemIds: itemIds.join(',') }) });
    } catch (error) {
      console.warn('Globe:', error);
      resume();
      note(text('failed'));
    }
  }

  const actionKeys = { Movie: 'movie', Series: 'series', Season: 'season', BoxSet: 'collection', Video: 'video', Photo: 'photo' };

  // The admin's table of actions per media type; whatever cannot run opens the detail page.
  async function onTile(item) {
    // Without Playnite Bridge a game has nowhere to go.
    if (item.type === 'Game' || item.type === 'GameCollection') return config.games ? onGame(item, item.type === 'Game' ? config.actions.game || 'Details' : 'Details') : undefined;
    const key = actionKeys[item.type];
    let action = key ? config.actions[key] : 'Details';
    if (action === 'Trailer') {
      const trailers = await api().getJSON(api().getUrl(`Users/${api().getCurrentUserId()}/Items/${item.id}/LocalTrailers`));
      if (trailers.length) return playNow(trailers.map(t => t.Id));
      // Like Jellyfin's trailer button: a trailer from the internet opens where it lives (mostly YouTube).
      const full = await api().getItem(api().getCurrentUserId(), item.id);
      const remote = full.RemoteTrailers?.find(t => t.Url)?.Url;
      if (remote) return void window.open(remote, '_blank', 'noopener');
      action = config.actions.noTrailer;
    }
    if (action === 'Play' || action === 'Viewer') return playNow([item.id]);
    hide();
    window.Emby.Page.show(`details?id=${item.id}&serverId=${api().serverId()}`);
  }

  // A game of Playnite Bridge: its buttons come from the Bridge, already fitted to this user and device, in the order of
  // its detail page. Play takes the first start or install button; without one Open in Playnite, else the detail page.
  // Messages go to the note line, as Jellyfin's own would sit behind the globe.
  async function onGame(item, action) {
    if (action !== 'Details') {
      try {
        const game = await api().getJSON(api().getUrl(`PlayniteBridge/games/${item.id}`));
        const version = game?.versions?.find(v => v.id === item.id) ?? game?.versions?.find(v => v.id === game.version);
        const actions = version?.actions ?? [];
        const plays = ['DirectStart', 'StartViaPlaynite', 'InstallDirect', 'InstallViaPlaynite'];
        const chosen = (action === 'Play' ? actions.find(a => plays.includes(a.kind)) : null) ?? actions.find(a => a.kind === 'OpenInPlaynite');
        if (chosen) return runGameAction(version.id, chosen);
      } catch (error) {
        console.warn('Globe:', error);
      }
    }
    hide();
    window.Emby.Page.show(`details?id=${item.id}&serverId=${api().serverId()}`);
  }

  // Like the Bridge's own buttons: a link goes to the browser, which hands it to the program registered for it; a server
  // action runs on the PC that holds Playnite.
  async function runGameAction(versionId, action) {
    if (action.via === 'Link') {
      const link = Object.assign(document.createElement('a'), { href: action.uri });
      document.body.appendChild(link);
      link.click();
      link.remove();
      return note(text('started'));
    }
    try {
      await api().ajax({ type: 'POST', url: api().getUrl(`PlayniteBridge/games/${versionId}/action/${action.kind}`) });
      note(text('started'));
    } catch (error) {
      console.warn('Globe:', error);
      note(text('failed'));
    }
  }

  // An animated picture plays over its tile once the globe has turned to it and the tile is large enough; the
  // animation covers only the part of the tile the picture fills, so a blurred fill around it stays.
  let animation = null, animatedTiles = null;
  function stopAnimation() {
    animation?.remove();
    animation = null;
  }

  async function playAnimation(entry, snap) {
    stopAnimation();
    if (!config.playAnimated || !snap.animated || snap.rect.height < 200) return;
    const ticket = opening;
    const headers = { Authorization: `MediaBrowser Token="${api().accessToken()}"` };
    try {
      if (!animatedTiles || animatedTiles.id !== entry.id) {
        const list = await fetch(api().getUrl(`Globe/data/${entry.id}/animated.json`), { headers });
        animatedTiles = { id: entry.id, map: list.ok ? await list.json() : {} };
      }
      const place = animatedTiles.map[snap.tile];
      if (!place) return;
      const file = await fetch(api().getUrl(`Globe/data/${entry.id}/original/${snap.tile}`), { headers });
      if (!file.ok || ticket !== opening || !layer) return;
      const url = URL.createObjectURL(await file.blob());
      const [sx, sy, sw, sh] = place.source, [tx, ty, tw, th] = place.target;
      const box = document.createElement('div');
      const w = snap.rect.width * tw, h = snap.rect.height * th;
      box.style.cssText = `position:absolute;z-index:1;overflow:hidden;pointer-events:none;left:${snap.rect.x + snap.rect.width * tx}px;`
        + `top:${snap.rect.y + snap.rect.height * ty}px;width:${w}px;height:${h}px;`;
      const picture = new Image();
      picture.style.cssText = `position:absolute;left:${-sx * w / sw}px;top:${-sy * h / sh}px;width:${w / sw}px;height:${h / sh}px;`;
      picture.onload = () => URL.revokeObjectURL(url);
      picture.src = url;
      box.appendChild(picture);
      stopAnimation();
      animation = box;
      layer.appendChild(box);
    } catch (error) {
      console.warn('Globe:', error);
    }
  }

  function message(lines) {
    const box = document.createElement('div');
    box.className = 'globeMessage';
    box.style.cssText = 'position:absolute;inset:0;display:flex;flex-direction:column;gap:6px;align-items:center;justify-content:center;'
      + 'color:#bbb;font:15px system-ui,sans-serif;text-align:center;padding:0 24px;';
    for (const line of lines) box.appendChild(Object.assign(document.createElement('div'), { textContent: line }));
    layer.appendChild(box);
  }

  async function show(entry) {
    const ticket = ++opening;
    globe?.dispose();
    globe = null;
    // The globe is loaded anew and may have been rebuilt, so its list of animated tiles is read again.
    stopAnimation();
    animatedTiles = null;
    layer.querySelectorAll('.globeMessage').forEach(m => m.remove());
    current = entry;
    layer.style.background = entry.color;
    updateMenu();
    if (!entry.ready) { message([text('notReady')]); return; }
    try {
      localStorage.setItem(lastUsedKey(), entry.id);
    } catch { /* storage may be blocked; only the remembered globe is lost */ }
    const base = api().getUrl('Globe/');
    const headers = { Authorization: `MediaBrowser Token="${api().accessToken()}"` };
    try {
      const { createGlobe } = await import(base + 'globe.js');
      if (ticket !== opening) return;
      const created = await createGlobe(layer, {
        base, globeId: entry.id, color: entry.color, startView: entry.startView, headers, onPlay: onTile,
        onSnap: snap => playAnimation(entry, snap), onMove: stopAnimation,
      });
      if (ticket !== opening) { created.dispose(); return; }
      globe = created;
    } catch (error) {
      if (ticket !== opening) return;
      console.warn('Globe:', error);
      message([text(error.unsupported ? 'unsupported' : 'notReady')]);
    }
  }

  // While the menu is open and a globe is being built, its progress follows every few seconds.
  let menuTimer = null;
  function toggleMenu(open) {
    menu?.classList.toggle('globeMenuOpen', open);
    if (menu) menu.style.transform = open ? 'translateX(0)' : 'translateX(100%)';
    clearInterval(menuTimer);
    menuTimer = open ? setInterval(async () => {
      if (!menu || !config.globes.some(g => g.state === 'building' || g.state === 'waiting')) return;
      const id = current?.id;
      await loadConfig().catch(() => {});
      current = config.globes.find(g => g.id === id) || current;
      updateMenu();
    }, 3000) : null;
  }

  function updateMenu() {
    if (!menu) return;
    const list = menu.querySelector('.globeMenuList');
    list.textContent = '';
    for (const entry of config.globes) {
      const item = document.createElement('button');
      item.type = 'button';
      const state = entry.state === 'building' ? ` – ${text('building')} ${entry.percent} %` : '';
      item.textContent = entry.name + state;
      item.disabled = !entry.ready;
      item.style.cssText = 'display:block;width:100%;text-align:left;padding:10px 16px;border:0;background:none;color:inherit;'
        + 'font:14px system-ui,sans-serif;cursor:pointer;' + (entry === current ? 'background:rgba(255,255,255,.12);' : '')
        + (entry.ready ? '' : 'opacity:.45;cursor:default;');
      item.addEventListener('click', () => { toggleMenu(false); if (entry !== current) show(entry); });
      list.appendChild(item);
    }
  }

  function buildMenu() {
    const tab = document.createElement('button');
    tab.type = 'button';
    tab.title = text('globes');
    tab.innerHTML = tabIcon;
    tab.style.cssText = 'position:absolute;top:64px;right:0;z-index:2;display:flex;align-items:center;justify-content:center;width:26px;'
      + 'height:52px;padding:0;border:0;border-radius:6px 0 0 6px;background:rgba(0,0,0,.55);color:#fff;opacity:.85;cursor:pointer;';
    tab.addEventListener('click', () => toggleMenu(!menu.classList.contains('globeMenuOpen')));
    menu = document.createElement('div');
    menu.style.cssText = 'position:absolute;top:0;right:0;bottom:0;z-index:3;width:280px;max-width:80vw;padding-top:64px;box-sizing:border-box;'
      + 'background:rgba(16,16,16,.92);color:#eee;transform:translateX(100%);transition:transform .25s ease;overflow-y:auto;';
    const list = document.createElement('div');
    list.className = 'globeMenuList';
    menu.appendChild(list);
    layer.appendChild(tab);
    layer.appendChild(menu);
    // A click beside the open menu closes it (the menu covers the tab).
    layer.addEventListener('pointerdown', e => {
      if (menu.classList.contains('globeMenuOpen') && !menu.contains(e.target) && e.target !== tab && !tab.contains(e.target)) toggleMenu(false);
    }, true);
  }

  function openingEntry() {
    const ready = config.globes.filter(g => g.ready);
    let remembered = null;
    try {
      remembered = localStorage.getItem(lastUsedKey());
    } catch { /* storage may be blocked */ }
    return ready.find(g => g.id === config.openingGlobe) || ready.find(g => g.id === remembered) || ready[0] || config.globes[0];
  }

  async function open() {
    if (layer) {
      if (away !== null) resume();
      return;
    }
    if (!api()) return;
    layer = document.createElement('div');
    layer.style.cssText = 'position:fixed;inset:0;z-index:100000;background:#000;overflow:hidden;';
    const closeButton = document.createElement('button');
    closeButton.type = 'button';
    closeButton.title = text('close');
    closeButton.innerHTML = closeIcon;
    closeButton.style.cssText = 'position:absolute;top:12px;right:12px;z-index:4;display:flex;padding:6px;border:0;border-radius:50%;'
      + 'background:rgba(0,0,0,.55);color:#fff;opacity:.85;cursor:pointer;';
    closeButton.addEventListener('click', () => close(true));
    layer.appendChild(closeButton);
    document.body.appendChild(layer);
    history.pushState({ globe: true }, '', location.href);
    addEventListener('keydown', onKey);

    const ticket = opening;
    try {
      await loadConfig();
    } catch (error) {
      console.warn('Globe:', error);
    }
    if (!layer || ticket !== opening) return;
    if (!config?.globes.length) {
      message(config?.isAdmin ? [text('noGlobe'), text('adminHint')] : [text('noGlobe')]);
      return;
    }
    if (config.globes.length > 1) buildMenu();
    show(openingEntry());
  }

  // Where the button goes: the legacy header, or the header of Jellyfin 12's modern layout, whose icons sit in a box beside
  // the user menu. There the button borrows the classes of the user menu button and the size of the icons beside it.
  function headerSlot() {
    const userMenu = document.querySelector('header.MuiAppBar-root [aria-controls="app-user-menu"]');
    if (userMenu?.getClientRects().length) {
      return { parent: userMenu.parentElement.previousElementSibling ?? userMenu.parentElement, className: userMenu.className, modern: true };
    }
    // Only a header that shows: during start-up the hidden legacy header is there before the modern one appears.
    const legacy = document.querySelector('.headerRight');
    return legacy?.getClientRects().length ? { parent: legacy, className: 'headerButton headerButtonRight paper-icon-button-light' } : null;
  }

  // The globe needs WebGL 2, S3TC textures and textures as wide as an atlas page; without them the button stays away,
  // for admins too (their way in is the plugin page).
  let suitable = null;
  function deviceSuits() {
    if (suitable === null) {
      try {
        const gl = document.createElement('canvas').getContext('webgl2');
        suitable = !!gl && !!gl.getExtension('WEBGL_compressed_texture_s3tc') && gl.getParameter(gl.MAX_TEXTURE_SIZE) >= 16384;
        gl?.getExtension('WEBGL_lose_context')?.loseContext();
      } catch {
        suitable = false;
      }
    }
    return suitable;
  }

  function addButton() {
    const slot = headerSlot();
    if (!slot || !api()?.getCurrentUserId() || !deviceSuits()) return;
    const existing = document.querySelector('.headerGlobeButton');
    if (configUser !== api().getCurrentUserId()) {
      // A new user: the old globe goes, and the button waits for what this user may see (no globe and no admin: no button).
      if (layer) close(false);
      configUser = api().getCurrentUserId();
      config = null;
      existing?.remove();
      loadConfig().then(addButton, () => {});
      return;
    }
    if (!config || existing?.parentElement === slot.parent) return;
    existing?.remove();
    if (!config.isAdmin && !config.globes.length) return;
    const button = document.createElement('button');
    button.type = 'button';
    button.title = text('title');
    button.setAttribute('aria-label', text('title'));
    button.className = slot.className + ' headerGlobeButton';
    button.innerHTML = globeIcon;
    if (slot.modern) {
      button.style.padding = '12px';
      button.firstChild.style.fontSize = '1.5rem';
    }
    button.addEventListener('click', open);
    slot.parent.insertBefore(button, slot.parent.firstChild);
  }

  new MutationObserver(addButton).observe(document.body, { childList: true, subtree: true });
  addButton();
})();
