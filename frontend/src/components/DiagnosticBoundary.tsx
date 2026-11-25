import { Component, type ReactNode } from 'react'
import { reportUiFailure } from '@/lib/diagnostics'

export default class DiagnosticBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  state = { failed: false }

  static getDerivedStateFromError() {
    return { failed: true }
  }

  componentDidCatch() {
    reportUiFailure('ANUI003')
  }

  render() {
    if (this.state.failed) {
      return (
        <main role="alert" className="min-h-screen flex flex-col items-center justify-center gap-4 px-6 bg-panel-darker text-white">
          <h1 className="text-xl font-semibold">Não foi possível carregar esta tela</h1>
          <p>Page rendering failed. Referência / Reference: ANUI003.</p>
          <button className="button button-primary" onClick={() => window.location.reload()}>
            Recarregar / Reload
          </button>
        </main>
      )
    }
    return this.props.children
  }
}
