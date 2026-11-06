/**
 * Golden-master fixtures shared with the .NET rewrite.
 *
 * A golden test computes a JSON-serializable value from the current TypeScript
 * behavior and compares it with `Tests/Golden/<name>.json`. The C# tests replay
 * the same files, so both implementations are held to one recorded truth.
 *
 * - `RINGVIEW_UPDATE_GOLDENS=1 npm test` (re)writes the fixtures.
 * - A missing fixture fails the test otherwise, so CI can never silently
 *   create one.
 * - Output is ASCII-only (non-ASCII characters are written as \uXXXX escapes)
 *   to satisfy the rewrite's ASCII convention tests.
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import { dirname, resolve } from 'node:path'
import { expect } from 'vitest'

const GOLDEN_DIR = resolve(import.meta.dirname, '../../../Tests/Golden')

function toAsciiJson(value: unknown): string {
  const json = JSON.stringify(value, null, 2)
  // Written without a regex escape on purpose: editors and formatters can turn
  // a typed escape into the literal character, which would make this file
  // non-ASCII.
  let out = ''
  for (const ch of json) {
    const code = ch.charCodeAt(0)
    out += code > 127 ? `\\u${code.toString(16).padStart(4, '0')}` : ch
  }
  return out + '\n'
}

/** Compare `value` with the named golden fixture (or rewrite it when updating). */
export function expectGolden(name: string, value: unknown): void {
  const path = resolve(GOLDEN_DIR, `${name}.json`)
  const text = toAsciiJson(value)
  if (process.env.RINGVIEW_UPDATE_GOLDENS === '1') {
    mkdirSync(dirname(path), { recursive: true })
    writeFileSync(path, text, 'utf8')
    return
  }
  if (!existsSync(path)) {
    throw new Error(
      `Golden fixture ${name}.json is missing. Run RINGVIEW_UPDATE_GOLDENS=1 npm test to create it.`
    )
  }
  const expected: unknown = JSON.parse(readFileSync(path, 'utf8'))
  // Round-trip the actual value so undefined fields compare like the file does.
  const actual: unknown = JSON.parse(text)
  expect(actual).toEqual(expected)
}
