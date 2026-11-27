import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { pathToFileURL } from 'node:url';

const [chrome, report, profile, screenshot] = process.argv.slice(2);
const browser = spawn(chrome, [
  '--headless=new', '--disable-gpu', '--disable-background-networking',
  '--disable-component-update', '--disable-extensions', '--no-first-run',
  '--no-default-browser-check', '--remote-debugging-port=0',
  '--user-data-dir=' + profile, 'about:blank',
], { windowsHide: true, stdio: 'ignore' });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const pending = new Map();
let socket;
let sequence = 0;

async function until(fn, message) {
  for (let attempt = 0; attempt < 120; attempt++) {
    if (browser.exitCode !== null) throw new Error('The isolated browser exited early');
    const value = await fn();
    if (value) return value;
    await delay(120);
  }
  throw new Error(message);
}

async function command(method, params = {}) {
  const id = ++sequence;
  const result = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
  socket.send(JSON.stringify({ id, method, params }));
  return result;
}

async function evaluate(expression) {
  const reply = await command('Runtime.evaluate', { expression, returnByValue: true });
  if (reply.exceptionDetails) throw new Error(reply.exceptionDetails.exception?.description ?? 'Browser evaluation failed');
  return reply.result.value;
}

try {
  const port = await until(async () => {
    try { return (await readFile(join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]; }
    catch (error) { if (error.code !== 'ENOENT') throw error; return null; }
  }, 'Browser debug port was not ready');
  const tabs = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json();
  const tab = tabs.find(item => item.type === 'page');
  assert(tab, 'No isolated Chromium page');
  socket = new WebSocket(tab.webSocketDebuggerUrl);
  socket.addEventListener('message', event => {
    const packet = JSON.parse(event.data);
    const request = pending.get(packet.id);
    if (!request) return;
    pending.delete(packet.id);
    if (packet.error) request.reject(new Error(packet.error.message));
    else request.resolve(packet.result);
  });
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
  const resize = (width, height) => command('Emulation.setDeviceMetricsOverride', { width, height, deviceScaleFactor: 1, mobile: false });
  await resize(1440, 900);
  await command('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'light' }] });
  await command('Page.navigate', { url: pathToFileURL(report).href });
  await until(() => evaluate("document.documentElement.classList.contains('interactive') && !!document.querySelector('.commit:not([hidden]) .tools:not([hidden])')"), 'The offline commit picker did not start');

  const invoke = (fn, ...args) => evaluate('(' + fn.toString() + ')(...' + JSON.stringify(args) + ')');
  const selectCommit = subject => invoke(subject => {
    const picker = document.querySelector('#commit-picker');
    const row = [...picker.tBodies[0].rows].find(candidate => candidate.querySelector('.commit-link').textContent.includes(subject));
    row.cells[3].click();
    return row.dataset.commit;
  }, subject);
  function inspectActive(fn, ...args) {
    return evaluate('(' + fn.toString() + ")(document.querySelector('.commit:not([hidden])'),..." + JSON.stringify(args) + ')');
  }

  assert.deepEqual(await invoke(() => {
    const picker = document.querySelector('#commit-picker');
    const rows = picker.tBodies[0].rows;
    const active = document.querySelectorAll('.commit:not([hidden])');
    return { choices: rows.length, active: active.length, selected: rows[0].classList.contains('selected') &&
      rows[0].dataset.commit === active[0].id,
      headings: [...picker.querySelectorAll('.column-title')].map(heading => heading.textContent),
      centeredHeadings: [...picker.tHead.rows[0].cells].every(cell => getComputedStyle(cell).textAlign === 'center'),
      ordinals: [...rows].map(row => Number(row.cells[0].textContent)),
      rowSeparator: getComputedStyle(rows[1].cells[0]).boxShadow !== 'none',
      columnSeparator: [...rows[0].cells].slice(0, -1).every((cell, i) => {
        const border = getComputedStyle(cell);
        const header = getComputedStyle(picker.tHead.rows[0].cells[i]);
        return border.borderRightWidth === '1px' && border.borderRightColor === header.borderRightColor;
      }),
      titled: /^main_demo_\d{8}_\d{6}$/.test(document.title) &&
        document.querySelector('.report-title').textContent === document.title,
      noContextLabelInTable: !picker.textContent.includes('Context lines') };
  }), { choices:8, active:1, selected:true, headings:['No.', 'Branch', 'Description', 'Date', 'Author', 'Commit'],
    centeredHeadings:true, ordinals:[1, 2, 3, 4, 5, 6, 7, 8], rowSeparator:true, columnSeparator:true,
    titled:true, noContextLabelInTable:true });

  const measureTable = () => invoke(() => {
    const picker = document.querySelector('#commit-picker');
    const frame = picker.parentElement;
    const rows = [...picker.tBodies[0].rows];
    const head = picker.tHead.rows[0].cells[0].getBoundingClientRect();
    const bottom = frame.getBoundingClientRect().bottom - frame.clientTop;
    return { top:head.top, height:frame.clientHeight, scrollTop:frame.scrollTop,
      fiveRows:(frame.clientHeight - head.height) / rows[0].getBoundingClientRect().height,
      hasScrollbar:getComputedStyle(frame).overflowY === 'scroll' && frame.scrollHeight > frame.clientHeight,
      fullyVisible:rows.filter(row => {
        const bounds = row.getBoundingClientRect();
        return bounds.top >= head.bottom - .1 && bounds.bottom <= bottom + .1;
      }).length };
  });
  const table = await measureTable();
  assert.equal(table.fiveRows, 5);
  assert.equal(table.fullyVisible, 5);
  assert.equal(table.hasScrollbar, true);
  const closedLastRow = () => invoke(() => {
    const picker = document.querySelector('#commit-picker');
    const row = picker.tBodies[0].rows.item(picker.tBodies[0].rows.length - 1);
    const headerColor = getComputedStyle(picker.tHead.rows[0].cells[0]).borderBottomColor;
    const area = picker.parentElement.getBoundingClientRect();
    const bottom = row.getBoundingClientRect().bottom;
    return bottom <= area.bottom && [...row.cells].every(cell => {
      const border = getComputedStyle(cell);
      return border.borderBottomWidth === '1px' && border.borderBottomColor === headerColor;
    });
  });
  await command('Input.dispatchMouseEvent', { type:'mouseWheel', x:520, y:106, deltaX:0, deltaY:60 });
  await until(async () => (await measureTable()).scrollTop > 0, 'Commit table did not scroll');
  assert.equal((await measureTable()).top, table.top, 'Column titles must stay pinned while the rows scroll');

  const measureColumns = () => invoke(() => {
    const picker = document.querySelector('#commit-picker');
    const widths = cells => [...cells].map(cell => cell.getBoundingClientRect().width);
    return { header:widths(picker.tHead.rows[0].cells), body:widths(picker.tBodies[0].rows[0].cells) };
  });
  async function drag(x, y, dx, dy) {
    await command('Input.dispatchMouseEvent', { type:'mousePressed', x, y, button:'left', buttons:1, clickCount:1 });
    await command('Input.dispatchMouseEvent', { type:'mouseMoved', x:x+dx, y:y+dy, button:'left', buttons:1 });
    await command('Input.dispatchMouseEvent', { type:'mouseReleased', x:x+dx, y:y+dy, button:'left', clickCount:1 });
  }
  for (const index of [0, 1, 2, 3, 4]) {
    const before = (await measureColumns()).header;
    const [x, y] = await invoke(index => {
      const bounds = document.querySelectorAll('.column-handle')[index].getBoundingClientRect();
      return [Math.round(bounds.left + 2), Math.round((bounds.top + bounds.bottom) / 2)];
    }, index);
    await drag(x, y, 26, 0);
    const { header, body } = await measureColumns();
    assert(Math.abs(header[index] - before[index] - 26) <= 1, 'Dragged header did not grow with the pointer');
    assert(Math.abs(before[index+1] - header[index+1] - 26) <= 1, 'Adjacent column did not resize');
    assert(header.every((width, i) => Math.abs(width - body[i]) < 1), 'Header and body columns became misaligned');
  }
  assert.equal((await measureTable()).fiveRows, 5);
  assert.equal(await invoke(() => {
    const picker = document.querySelector('#commit-picker');
    const last = picker.tBodies[0].rows.item(picker.tBodies[0].rows.length - 1);
    picker.querySelector('.selected .commit-link')
      .dispatchEvent(new KeyboardEvent('keydown', { key:'End', bubbles:true }));
    return last.classList.contains('selected') && location.hash === '#' + last.dataset.commit;
  }), true, 'Keyboard navigation to the bottom must select and reveal the last commit');
  assert.equal(await closedLastRow(), true, 'The last commit row must have a bottom border after scrolling');
  const preview = await selectCommit('Tune sensor acquisition');
  assert.equal(await evaluate('location.hash'), '#' + preview);

  const measurePanes = () => inspectActive(commit => {
    const rect = selector => commit.querySelector(selector).getBoundingClientRect();
    return { table:document.querySelector('.commit-scroll').offsetHeight, editor:rect('.diffs').height,
      metadata:rect('.commit-meta').height, files:rect('.files').height,
      left:rect('.inspector').width, right:rect('.diffs').width };
  });
  const dragPane = async (name, dx, dy) => {
    const [x, y] = await inspectActive((commit, name) => {
      const scope = name === 'commits' ? document : commit;
      const rect = scope.querySelector('[data-pane=' + name + ']').getBoundingClientRect();
      return [Math.round(rect.left + rect.width / 2), Math.round(rect.top + rect.height / 2)];
    }, name);
    await drag(x, y, dx, dy);
  };
  const initialPanes = await measurePanes();
  await dragPane('commits', 0, 37);
  const lower = await measurePanes();
  assert.equal(lower.table - initialPanes.table, 37);
  assert.equal(initialPanes.editor - lower.editor, 37);
  await dragPane('metadata', 0, 34);
  const taller = await measurePanes();
  assert(Math.abs(taller.metadata - lower.metadata - 34) <= 1);
  assert(Math.abs(lower.files - taller.files - 34) <= 1);
  await dragPane('inspector', 41, 0);
  const wider = await measurePanes();
  assert(Math.abs(wider.left - taller.left - 41) <= 1);
  assert(Math.abs(taller.right - wider.right - 41) <= 1);
  await selectCommit('Change sensor line');
  assert.deepEqual(await measurePanes(), wider);
  await selectCommit('Tune sensor acquisition');
  await invoke(() => {
    for (const name of ['--inspector-width', '--metadata-height', '--commit-height'])
      document.querySelector('main').style.removeProperty(name);
  });
  assert.deepEqual(await measurePanes(), initialPanes);

  const layout = await inspectActive(commit => {
    const rect = selector => commit.querySelector(selector).getBoundingClientRect();
    const files = rect('.files'), meta = rect('.commit-meta'), diffs = rect('.diffs');
    const file = commit.querySelector('.diff-file:not([hidden])');
    const row = file.querySelector('.line.add');
    const code = row.querySelector('code');
    const token = row.querySelector('.token');
    return { panes: files.right < diffs.left && meta.bottom <= files.top && meta.top === diffs.top,
      diffDominates: diffs.width >= files.width * 3 && diffs.height > innerHeight * .75,
      contextInInspector: commit.querySelector('.commit-meta').textContent.includes('Context lines20'),
      branchInInspector: commit.querySelector('.commit-branches').textContent.trim() === 'review/renamed',
      versions: file.querySelector('.versions').children.length,
      visibleFiles: commit.querySelectorAll('.diff-file:not([hidden])').length,
      rowHeight: row.getBoundingClientRect().height, hunkHeight: file.querySelector('.hunk').getBoundingClientRect().height,
      marker: row.querySelector('.mark').textContent,
      oldNew: row.querySelectorAll('.number').length,
      selectable: getComputedStyle(code).userSelect !== 'none',
      colorsDistinct: new Set([row, row.querySelector('.number'), file.querySelector('.line.remove'),
        file.querySelector('.line.context'), file.querySelector('.hunk'), token]
        .map(item => getComputedStyle(item).backgroundColor)).size === 6,
      changedText: token.textContent,
      horizontalOverflowStaysInFile: document.documentElement.scrollWidth <= document.documentElement.clientWidth };
  });
  assert.deepEqual(layout, { panes:true, diffDominates:true, contextInInspector:true, branchInInspector:true,
    versions:2, visibleFiles:1, rowHeight:20, hunkHeight:22,
    marker:'+', oldNew:2, selectable:true, colorsDistinct:true, changedText:'40', horizontalOverflowStaysInFile:true });

  assert.deepEqual(await inspectActive(commit => {
    const file = commit.querySelector('.diff-file:not([hidden])');
    const counter = file.querySelector('.position');
    const values = [counter.textContent];
    file.querySelector('.next').click();
    values.push(counter.textContent);
    file.querySelector('.previous').click();
    values.push(counter.textContent);
    return values;
  }), ['1/2', '2/2', '1/2']);

  const applyFilter = value => inspectActive((commit, value) => {
    const input = commit.querySelector('.filter input');
    input.value = value;
    input.dispatchEvent(new Event('input'));
    return [...commit.querySelectorAll('.diff-file:not([hidden])')].map(file =>
      file.querySelector('.file-path bdi:last-child').textContent);
  }, value);
  assert.deepEqual(await applyFilter('FIRMWARE/SETTINGS'), ['firmware/settings.toml']);
  assert.deepEqual(await applyFilter('no-match-fixture'), []);
  assert.equal(await inspectActive(commit => !commit.querySelector('.no-matches').hidden), true);
  assert.deepEqual(await applyFilter(''), ['firmware/sampler.py']);

  assert.equal(await inspectActive(commit => {
    const link = [...commit.querySelectorAll('.tree a')].find(item => item.title.endsWith('settings.toml'));
    link.click();
    const file = commit.querySelector('.diff-file:not([hidden])');
    const patch = file.querySelector('.patch');
    const wrap = file.querySelector('.wrap');
    const originallyScrollable = patch.scrollWidth > patch.clientWidth;
    wrap.click();
    const wrapped = wrap.getAttribute('aria-pressed') === 'true' &&
      getComputedStyle(patch.querySelector('.source')).whiteSpace === 'pre-wrap' && patch.scrollWidth <= patch.clientWidth;
    wrap.click();
    return file.id === link.hash.slice(1) && link.hasAttribute('aria-current') && originallyScrollable && wrapped;
  }), true);

  const changed = await selectCommit('Change sensor line');
  assert.deepEqual(await inspectActive(commit => ({
    id: commit.id, files: [...commit.querySelectorAll('.tree a')].map(link => link.title),
    showsChangedDiff: !!commit.querySelector('.diff-file:not([hidden]) .line.add')
  })), { id:changed, files:['sensor.txt'], showsChangedDiff:true });
  const empty = await selectCommit('Empty checkpoint');
  assert.deepEqual(await inspectActive(commit => ({
    id:commit.id, empty:!!commit.querySelector('.empty:not([hidden])'), files:commit.querySelectorAll('.diff-file').length
  })), { id:empty, empty:true, files:0 });
  await evaluate('history.back()');
  await until(() => inspectActive(commit => commit.querySelector('.message').textContent.includes('Change sensor line')), 'Browser history did not restore the previous commit');

  await selectCommit('Rename and update binary');
  const selectFile = name => inspectActive((commit, name) => {
    const link = [...commit.querySelectorAll('.tree a')].find(item => item.querySelector('bdi').textContent === name);
    link.click();
    return commit.querySelector('.diff-file:not([hidden])').id === link.hash.slice(1);
  }, name);
  for (const name of ['record.bin', 'added.bin', 'removed.bin', 'after.bin']) {
    assert.equal(await selectFile(name), true);
    assert.deepEqual(await inspectActive(commit => {
      const file = commit.querySelector('.diff-file:not([hidden])');
      const diff = commit.querySelector('.diffs');
      const mask = file.querySelector('.binary-mask');
      const label = mask.querySelector('span');
      const area = mask.getBoundingClientRect(), frame = diff.getBoundingClientRect(), text = label.getBoundingClientRect();
      return { label:label.textContent, fontSize:getComputedStyle(label).fontSize,
        fillsPane:Math.abs(area.width - frame.width) <= 2 && Math.abs(area.height - frame.height) <= 2,
        centered:Math.abs(text.left + text.width / 2 - area.left - area.width / 2) <= 1 &&
          Math.abs(text.top + text.height / 2 - area.top - area.height / 2) <= 1,
        shaded:getComputedStyle(mask).backgroundColor !== getComputedStyle(diff).backgroundColor,
        elements:file.children.length, textDiff:!!file.querySelector('.patch, .tools') };
    }), { label:'Binary file', fontSize:'30px', fillsPane:true, centered:true, shaded:true, elements:1, textDiff:false });
  }

  await selectCommit('Tune sensor acquisition');
  assert.equal(await inspectActive(commit => commit.querySelector('.tree a[aria-current]').title), 'firmware/settings.toml');
  await inspectActive(commit => commit.querySelector('.tree a').click());

  await resize(520, 900);
  await until(() => evaluate("matchMedia('(max-width:700px)').matches"), 'Narrow browser viewport was not applied');
  assert.equal(await inspectActive(commit => {
    const tree = commit.querySelector('.files').getBoundingClientRect(), diff = commit.querySelector('.diffs').getBoundingClientRect();
    return tree.bottom <= diff.top && document.documentElement.scrollWidth <= document.documentElement.clientWidth;
  }), true, 'Narrow layout must stack the inspector above the diff');

  await resize(1440, 900);
  await command('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'dark' }] });
  assert.equal(await inspectActive(commit => {
    const file = commit.querySelector('.diff-file:not([hidden])');
    const colors = ['.line.context', '.line.add', '.line.remove', '.hunk'].map(selector => getComputedStyle(file.querySelector(selector)).backgroundColor);
    return matchMedia('(prefers-color-scheme:dark)').matches && new Set(colors).size === 4;
  }), true);
  await command('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: 'light' }] });
  await invoke(() => {
    for (const column of document.querySelectorAll('#commit-picker col')) column.style.removeProperty('width');
    document.activeElement.blur();
    window.scrollTo(0, 0);
  });
  await command('Input.dispatchMouseEvent', { type:'mouseMoved', x:1438, y:899 });
  await delay(180);
  const shot = await command('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  await writeFile(screenshot, Buffer.from(shot.data, 'base64'));
  await command('Page.navigate', { url:new URL('default.html', pathToFileURL(report)).href });
  await until(() => invoke(() => document.querySelectorAll('#commit-picker tbody tr').length === 1), 'Single-commit report did not load');
  const single = await measureTable();
  assert.equal(single.height, table.height);
  assert.equal(single.fiveRows, 5);
  assert.equal(single.fullyVisible, 1);
  assert.equal(await closedLastRow(), true, 'A single commit row must have a bottom border');
  await command('Page.navigate', { url:new URL('localized-zh_CN.html', pathToFileURL(report)).href });
  await until(() => invoke(() => document.documentElement.lang === 'zh-CN' &&
    document.documentElement.classList.contains('interactive')), 'Simplified Chinese report did not load');
  await selectCommit('Rename and update binary');
  assert.equal(await selectFile('record.bin'), true);
  assert.equal(await inspectActive(commit => commit.querySelector('.diff-file:not([hidden]) .binary-mask span').textContent), '二进制文件');
  await invoke(() => document.activeElement.blur());
  const maskShot = await command('Page.captureScreenshot', { format:'png', captureBeyondViewport:false });
  await writeFile(screenshot.replace(/\.png$/, '-binary.png'), Buffer.from(maskShot.data, 'base64'));
  console.log('PASS Chromium checked table dividers and the centered gray mask for binary entries');
} finally {
  if (socket?.readyState === WebSocket.OPEN) {
    socket.send(JSON.stringify({ id: ++sequence, method: 'Browser.close' }));
    socket.close();
  }
  if (browser.exitCode === null) await Promise.race([new Promise(resolve => browser.once('exit', resolve)), delay(3000)]);
  if (browser.exitCode === null) browser.kill();
}
