import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import api, { User } from "../api";

export const useAuth = () => {
  const [user, setUser] = useState<User | null>(null);
  const [authLoading, setAuthLoading] = useState(true);
  const navigate = useNavigate();

  useEffect(() => {
    let cancelled = false;
    const bootstrap = async () => {
      try {
        const token = localStorage.getItem("access_token");
        if (token) {
          const me = await api.get("/auth/me");
          if (!cancelled) setUser(me.data);
        }
      } catch (err) {
        localStorage.removeItem("access_token");
        if (!cancelled) setUser(null);
      } finally {
        if (!cancelled) setAuthLoading(false);
      }
    };
    bootstrap();
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    const handleLogout = () => {
      setUser(null);
      setAuthLoading(false);
      navigate("/desktop/login");
    };
    window.addEventListener("auth:logout", handleLogout);
    return () => window.removeEventListener("auth:logout", handleLogout);
  }, [navigate]);

  const login = async (login: string, password: string, redirectTo?: string) => {
    const resp = await api.post("/auth/login", { login, password });
    localStorage.setItem("access_token", resp.data.access_token);
    setUser(resp.data.user ?? (await api.get("/auth/me")).data);
    navigate(redirectTo || "/");
  };

  const logout = async () => {
    await api.post("/auth/logout");
    localStorage.removeItem("access_token");
    setUser(null);
    navigate("/login");
  };

  return { user, setUser, login, logout, authLoading };
};
