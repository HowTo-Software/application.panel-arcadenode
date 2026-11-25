export function hasServerPermission(permissions: readonly string[] | null | undefined, permission: string) {
  return permissions == null || permissions.includes(permission)
}
