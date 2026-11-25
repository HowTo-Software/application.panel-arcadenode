import type { Server } from '@/types'

export function gameVisual(
  server: Pick<Server, 'name' | 'description' | 'dockerImage'>
) {
  const value =
    `${server.dockerImage} ${server.name} ${server.description}`.toLowerCase()
  if (/zomboid/.test(value))
    return {
      key: 'zomboid',
      label: 'Project Zomboid',
      cover: '/games/zomboid-cover.png',
      icon: '/games/zomboid-icon.png',
      short: 'PZ',
    }
  if (/bedrock/.test(value))
    return {
      key: 'bedrock',
      label: 'Minecraft Bedrock',
      cover: '/games/minecraft-cover.png',
      icon: '/games/minecraft-icon.png',
      short: 'MC',
    }
  if (/minecraft|paper|spigot|fabric/.test(value))
    return {
      key: 'minecraft',
      label: 'Minecraft Java',
      cover: '/games/minecraft-cover.png',
      icon: '/games/minecraft-icon.png',
      short: 'MC',
    }
  if (/arma|reforger/.test(value))
    return {
      key: 'arma',
      label: 'Arma Reforger',
      cover: '/games/arma-cover.png',
      icon: '',
      short: 'AR',
    }
  return {
    key: 'generic',
    label: 'Servidor de jogo',
    cover: '',
    icon: '',
    short: 'GS',
  }
}
