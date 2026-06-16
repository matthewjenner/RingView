// electron-builder afterPack hook.
//
// electron-builder's node-modules collector strips nested `node_modules`
// directories. A few deps use an INTERNAL node_modules folder for bare-specifier
// requires (e.g. @shinyoshiaki/binary-data, via werift/ring-client-api, does
// `require('lib/binary-stream')` resolved from its own src/node_modules). Those
// packages are asarUnpack'd (see electron-builder.yml); here we copy their
// internal node_modules back onto the unpacked filesystem so the requires work.

const fs = require('node:fs')
const path = require('node:path')

// Paths (relative to the project node_modules) of internal node_modules folders
// that must be restored. Keep in sync with the asarUnpack globs.
const INTERNAL_NODE_MODULES = ['@shinyoshiaki/binary-data/src/node_modules']

const projectRoot = path.join(__dirname, '..')

/** @param {{ appOutDir: string }} context */
exports.default = async function afterPack(context) {
  // Plain-files build (asar:false) -> resources/app/node_modules; asar build ->
  // resources/app.asar.unpacked/node_modules. Use whichever exists.
  const plain = path.join(context.appOutDir, 'resources', 'app', 'node_modules')
  const unpacked = path.join(context.appOutDir, 'resources', 'app.asar.unpacked', 'node_modules')
  const nmRoot = fs.existsSync(plain) ? plain : unpacked

  for (const rel of INTERNAL_NODE_MODULES) {
    const src = path.join(projectRoot, 'node_modules', rel)
    const dest = path.join(nmRoot, rel)
    if (!fs.existsSync(src)) {
      console.warn(`[after-pack] internal node_modules source not found: ${src}`)
      continue
    }
    fs.mkdirSync(path.dirname(dest), { recursive: true })
    fs.cpSync(src, dest, { recursive: true })
    console.log(`[after-pack] restored internal node_modules: ${rel}`)
  }
}
