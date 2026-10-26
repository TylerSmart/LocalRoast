import { HttpClient } from '@angular/common/http';
import { Service, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { apiUrl } from '../../api/api-url';
import { UploadedMediaDto, UploadedRecipeCoverDto } from '../../models/media.models';

@Service()
export class MediaService {
  private readonly http = inject(HttpClient);

  uploadRecipeCover(file: File): Observable<UploadedRecipeCoverDto> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    return this.http.post<UploadedRecipeCoverDto>(apiUrl('/media/recipe-cover'), formData);
  }

  uploadStepImage(file: File): Observable<UploadedMediaDto> {
    const formData = new FormData();
    formData.append('file', file, file.name);
    return this.http.post<UploadedMediaDto>(apiUrl('/media/recipe-step'), formData);
  }
}
