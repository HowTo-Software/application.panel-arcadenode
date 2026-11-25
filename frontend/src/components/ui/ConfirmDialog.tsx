import { useEffect, useId, useRef } from 'react'
import { ExclamationTriangleIcon } from '@heroicons/react/24/outline'

export function ConfirmDialog({
  open,
  title,
  description,
  pending,
  confirmLabel = 'Excluir servidor',
  pendingLabel = 'Excluindo...',
  onCancel,
  onConfirm,
}: {
  open: boolean
  title: string
  description: string
  pending: boolean
  confirmLabel?: string
  pendingLabel?: string
  onCancel: () => void
  onConfirm: () => void
}) {
  const ref = useRef<HTMLDialogElement>(null)
  const titleId = useId()
  const descriptionId = useId()
  useEffect(() => {
    if (open) ref.current?.showModal()
    else ref.current?.close()
  }, [open])
  return (
    <dialog
      ref={ref}
      className="confirm-dialog"
      aria-labelledby={titleId}
      aria-describedby={descriptionId}
      onCancel={(event) => {
        event.preventDefault()
        if (!pending) onCancel()
      }}
    >
      <div className="confirm-icon">
        <ExclamationTriangleIcon />
      </div>
      <h2 id={titleId}>{title}</h2>
      <p id={descriptionId}>{description}</p>
      <div className="dialog-actions">
        <button
          className="button button-secondary"
          autoFocus
          disabled={pending}
          onClick={onCancel}
        >
          Cancelar
        </button>
        <button
          className="button button-danger"
          disabled={pending}
          onClick={onConfirm}
        >
          {pending ? pendingLabel : confirmLabel}
        </button>
      </div>
    </dialog>
  )
}
