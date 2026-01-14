import axios from "axios";

const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL || "/api",
  withCredentials: true,
});

api.interceptors.request.use((config) => {
  const token = localStorage.getItem("access_token");
  if (token) {
    config.headers = config.headers || {};
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

api.interceptors.response.use(
  (resp) => resp,
  async (error) => {
    if (error.response?.status === 401 && !error.config.__retry) {
        error.config.__retry = true;
        try {
          const refresh = await api.post("/auth/refresh");
          const token = refresh.data.access_token;
          localStorage.setItem("access_token", token);
          error.config.headers.Authorization = `Bearer ${token}`;
          return api.request(error.config);
        } catch (e) {
          localStorage.removeItem("access_token");
          window.dispatchEvent(new Event("auth:logout"));
        }
    }
    return Promise.reject(error);
  }
);

export interface User {
  id: number;
  login: string;
  role: "admin" | "worker" | "viewer";
  is_active: boolean;
}

export default api;
