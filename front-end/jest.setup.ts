import '@testing-library/jest-dom';

// jsdom has no ResizeObserver. The Zest split button uses one to size its menu, so every card that renders it would
// crash without this. A plain class (not jest.fn()) survives tests that call jest.resetAllMocks().
if (typeof window !== 'undefined' && !('ResizeObserver' in window)) {
  class ResizeObserverMock {
    observe() { /* layout is not measured in jsdom */ }
    unobserve() { /* layout is not measured in jsdom */ }
    disconnect() { /* layout is not measured in jsdom */ }
  }
  Object.defineProperty(globalThis, 'ResizeObserver', { writable: true, value: ResizeObserverMock });
  Object.defineProperty(window, 'ResizeObserver', { writable: true, value: ResizeObserverMock });
}

// A plain function, not jest.fn(): tests that call jest.resetAllMocks() must not wipe it (ZestButton reads it on every mount).
if (typeof window !== 'undefined' && !window.matchMedia) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: (query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => undefined,
      removeListener: () => undefined,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      dispatchEvent: () => false,
    }),
  });
}
