import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';

export interface InfrastructureStatus {
  postgres: string;
  valkey: string;
  valkeyLatencyMs: number;
}

@Injectable({
  providedIn: 'root'
})
export class InfrastructureService {
  private readonly http = inject(HttpClient);

  getStatus() {
    return this.http.get<InfrastructureStatus>('/api/infrastructure');
  }
}