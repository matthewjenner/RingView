import js from '@eslint/js'
import prettier from 'eslint-config-prettier'
import importPlugin from 'eslint-plugin-import'
import react from 'eslint-plugin-react'
import reactHooks from 'eslint-plugin-react-hooks'
import globals from 'globals'
import tseslint from 'typescript-eslint'

export default tseslint.config(
  {
    ignores: ['out/**', 'dist/**', 'release/**', 'node_modules/**', '**/*.d.ts']
  },

  js.configs.recommended,
  ...tseslint.configs.strictTypeChecked,
  ...tseslint.configs.stylisticTypeChecked,

  // Type-aware linting for all source. projectService discovers the tsconfigs.
  {
    languageOptions: {
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname
      }
    },
    plugins: { import: importPlugin },
    rules: {
      // House style: named exports only (config files are exempted below).
      'import/no-default-export': 'error',
      '@typescript-eslint/no-explicit-any': 'error',
      // Surface unused code (helps the dead-code sweep); ignore _-prefixed args.
      '@typescript-eslint/no-unused-vars': [
        'error',
        { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }
      ],
      // Allow intentional fire-and-forget when explicitly marked with `void`.
      '@typescript-eslint/no-floating-promises': ['error', { ignoreVoid: true }],
      // Numbers in template strings are fine.
      '@typescript-eslint/restrict-template-expressions': ['error', { allowNumber: true }],
      // Arrow handlers that return a void call (e.g. onClick={() => setX()}) are idiomatic.
      '@typescript-eslint/no-confusing-void-expression': ['error', { ignoreArrowShorthand: true }],
      // Async event handlers are fine in JSX attributes.
      '@typescript-eslint/no-misused-promises': [
        'error',
        { checksVoidReturn: { attributes: false } }
      ],
      // Empty arrow no-ops (default callbacks, ignored .catch) are intentional.
      '@typescript-eslint/no-empty-function': ['error', { allow: ['arrowFunctions'] }],
      // Pedantic and fights legitimate generic helper signatures.
      '@typescript-eslint/no-unnecessary-type-parameters': 'off'
    }
  },

  // Main / preload / scripts run in Node.
  {
    files: ['src/main/**/*.ts', 'src/preload/**/*.ts', 'scripts/**/*.ts'],
    languageOptions: { globals: { ...globals.node } }
  },

  // Renderer is React in the browser.
  {
    files: ['src/renderer/**/*.{ts,tsx}'],
    ...react.configs.flat.recommended,
    languageOptions: {
      globals: { ...globals.browser }
    },
    settings: { react: { version: 'detect' } },
    plugins: { react, 'react-hooks': reactHooks },
    rules: {
      ...react.configs.flat.recommended.rules,
      'react/react-in-jsx-scope': 'off', // new JSX transform
      'react/prop-types': 'off', // TypeScript handles prop types
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'warn'
    }
  },

  // Tests: a few type-checked rules are noise in test code.
  {
    files: ['**/*.test.ts'],
    rules: {
      '@typescript-eslint/no-non-null-assertion': 'off',
      '@typescript-eslint/no-confusing-void-expression': 'off'
    }
  },

  // Config files: framework APIs require default exports and live outside tsconfig.
  {
    files: ['**/*.config.{ts,js}', 'eslint.config.js'],
    extends: [tseslint.configs.disableTypeChecked],
    languageOptions: { globals: { ...globals.node } },
    rules: {
      'import/no-default-export': 'off'
    }
  },

  prettier
)
