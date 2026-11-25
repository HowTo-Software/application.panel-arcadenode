import axios from 'axios'
import { useAuthStore } from '@/stores/authStore'
import { reportApiFailure } from '@/lib/diagnostics'

const API_URL = import.meta.env.VITE_API_URL || '/api'

const api = axios.create({
  baseURL: API_URL,
  headers: {
    'Content-Type': 'application/json',
  },
  withCredentials: false, // Authentication is exclusively an explicit Bearer header.
  timeout: 15000,
})

// Authentication uses an explicit Bearer header.
api.interceptors.request.use(
  (config) => {
    // Attach JWT bearer token
    const token = useAuthStore.getState().token
    if (token) {
      config.headers.Authorization = `Bearer ${token}`
    }

    return config
  },
  (error) => {
    return Promise.reject(error)
  }
)

// Response interceptor to handle auth errors
api.interceptors.response.use(
  (response) => response,
  (error) => {
    reportApiFailure(error)
    const isLoginRequest = error.config?.url === '/auth/login'
    if (error.response?.status === 401 && !isLoginRequest) {
      useAuthStore.getState().logout()
      window.location.href = '/login'
    }
    return Promise.reject(error)
  }
)

export default api
