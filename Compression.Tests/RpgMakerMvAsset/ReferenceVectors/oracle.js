// Drives Petschko's RPG-Maker-MV-Decrypter (MIT) scripts/Decrypter.js as a black-box oracle.
// usage: node oracle.js <dir-with-Decrypter.js> encrypt|decrypt|restorepng <keyhex|-> <in> <out>
const fs = require('fs'), vm = require('vm'), path = require('path');
const [dir, mode, key, input, output] = process.argv.slice(2);
const ctx = vm.createContext({});
vm.runInContext(fs.readFileSync(path.join(dir, 'ErrorException.js'), 'utf8'), ctx);
vm.runInContext(fs.readFileSync(path.join(dir, 'Decrypter.js'), 'utf8'), ctx);
const bytes = fs.readFileSync(input);
ctx.buf = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
ctx.key = key === '-' ? null : key;
const code = {
  encrypt: 'new Decrypter(key).encrypt(buf)',
  decrypt: 'new Decrypter(key).decrypt(buf)',
  restorepng: 'new Decrypter(null).restorePngHeader(buf)',
}[mode];
const out = vm.runInContext(code, ctx);
fs.writeFileSync(output, Buffer.from(out));
