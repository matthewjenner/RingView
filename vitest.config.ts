import { defineConfig } from 'vitest/config'

// Unit tests for the main process and shared code run in Node. Renderer/React
// component tests, if added later, can opt into a jsdom environment per-file
// via a `// @vitest-environment jsdom` pragma.
export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.{test,spec}.ts'],
    exclude: ['node_modules', 'out', 'dist']
  }
})
