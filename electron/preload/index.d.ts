import type { RingViewApi } from '@shared/types'

declare global {
  interface Window {
    ringview: RingViewApi
  }
}

export {}
