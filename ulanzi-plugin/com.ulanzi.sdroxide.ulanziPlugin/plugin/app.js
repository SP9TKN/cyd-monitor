// Passes the dial's events on to the CYD server, which does all the work (DialBridge.cs).
// An HTML main service on purpose: it adds no node.exe to this PC, and Ulanzi's installer
// kills those by name, the CWOP dashboard's included.

const MAIN = 'com.ulanzi.ulanzistudio.sdroxide';
const BRIDGE = 'http://127.0.0.1:7792';

const KEYS = {
  radio2m: '/radio/0',
  radio70cm: '/radio/1',
  show: '/show',
  hide: '/hide',
  volup: '/vol/1',
  voldown: '/vol/-1',
};
const ROTATION = {
  left: '/tune/-1',
  right: '/tune/1',
  'hold-left': '/hold/-1',
  'hold-right': '/hold/1',
};

const args = new URLSearchParams(location.search);

function tell(path) {
  // Nothing here reads the answer, so no CORS to satisfy.
  fetch(BRIDGE + path, { mode: 'no-cors', cache: 'no-store' }).catch(() => {});
}

// A press of the knob counts only if it was not the start of a push-and-turn, and the same goes
// for the scan button, which held turns the knob into the filter width. Worked out here and
// not in the server: only here are the events still in order.
let turnedSincePress = false;
let scanHeld = false;
let turnedWhileHeld = false;

function onEvent(msg) {
  const action = String(msg.uuid || '').split('.').pop();
  if (msg.cmd === 'keydown' && action === 'scan') {
    scanHeld = true;
    turnedWhileHeld = false;
  } else if (msg.cmd === 'keyup' && action === 'scan') {
    scanHeld = false;
    if (!turnedWhileHeld) tell('/scan');
  } else if (msg.cmd === 'keydown' && KEYS[action]) {
    tell(KEYS[action]);
  } else if (msg.cmd === 'dialdown') {
    turnedSincePress = false;
  } else if (msg.cmd === 'dialrotate' && ROTATION[msg.rotateEvent]) {
    turnedSincePress = true;
    if (scanHeld) {
      turnedWhileHeld = true;
      tell(msg.rotateEvent.endsWith('right') ? '/filter/1' : '/filter/-1');
    } else {
      tell(ROTATION[msg.rotateEvent]);
    }
  } else if (msg.cmd === 'dialup' && !turnedSincePress) {
    tell('/press');
  }
}

function connect() {
  const ws = new WebSocket(`ws://${args.get('address') || '127.0.0.1'}:${args.get('port') || 3906}`);
  ws.onopen = () => ws.send(JSON.stringify({ code: 0, cmd: 'connected', uuid: MAIN, key: '', actionid: '' }));
  ws.onclose = () => setTimeout(connect, 2000);
  ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    if (!msg || (msg.code !== undefined && msg.cmdType !== 'REQUEST')) return;
    // Studio expects the main service to answer every event.
    ws.send(JSON.stringify({ uuid: MAIN, key: '', actionid: '', ...msg, code: 0 }));
    onEvent(msg);
  };
}

connect();
