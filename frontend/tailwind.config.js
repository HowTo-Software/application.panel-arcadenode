/** @type {import('tailwindcss').Config} */
export default {
  content: [
    "./index.html",
    "./src/**/*.{js,ts,jsx,tsx}",
  ],
  darkMode: 'class',
  theme: {
    extend: {
      colors: {
        primary: {
          50: '#faf5ff',
          100: '#f3e8ff',
          200: '#e9d5ff',
          300: '#d8b4fe',
          400: '#b069ff',
          500: '#9b4dfb',
          600: '#8b32e0',
          700: '#7c3aed',
          800: '#6b21a8',
          900: '#581c87',
          950: '#3b0764',
        },
        panel: {
          dark: '#0c1020',
          darker: '#080b14',
          card: '#101626',
          border: '#232c43',
        },
        accent: {
          zomboid: '#4ade80',
          arma: '#f97316',
          minecraft: '#84cc16',
        }
      },
    },
  },
  plugins: [],
}
