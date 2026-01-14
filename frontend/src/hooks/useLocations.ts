import { useEffect, useState } from "react";
import api from "../api";
import { LocationItem } from "../types";

export const useLocations = (ready = true) => {
  const [locations, setLocations] = useState<LocationItem[]>([]);
  const [version, setVersion] = useState(0);
  useEffect(() => {
    if (!ready) return;
    api
      .get("/locations")
      .then((r) => setLocations(r.data))
      .catch(() => setLocations([]));
  }, [ready, version]);
  const reloadLocations = () => setVersion((v) => v + 1);
  return { locations, reloadLocations };
};
