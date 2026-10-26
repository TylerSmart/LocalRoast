import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { firstValueFrom } from 'rxjs';
import { UploadedMediaDto, UploadedRecipeCoverDto } from '../../models/media.models';
import { MediaService } from './media.service';

describe('MediaService', () => {
  let service: MediaService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), MediaService],
    });

    service = TestBed.inject(MediaService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('uploads recipe cover via POST /api/media/recipe-cover', async () => {
    const mockFile = new File(['dummy content'], 'cover.png', { type: 'image/png' });
    const responseData: UploadedRecipeCoverDto = {
      coverImagePath: '/api/uploads/recipes/token-cover.webp',
      thumbnailImagePath: '/api/uploads/recipes/token-thumb.webp',
    };

    const promise = firstValueFrom(service.uploadRecipeCover(mockFile));
    const req = httpMock.expectOne('/api/media/recipe-cover');
    expect(req.request.method).toBe('POST');
    req.flush(responseData);

    const result = await promise;
    expect(result.coverImagePath).toBe('/api/uploads/recipes/token-cover.webp');
    expect(result.thumbnailImagePath).toBe('/api/uploads/recipes/token-thumb.webp');
  });

  it('uploads step image via POST /api/media/recipe-step', async () => {
    const mockFile = new File(['dummy step'], 'step.png', { type: 'image/png' });
    const responseData: UploadedMediaDto = {
      imagePath: '/api/uploads/steps/step-token.webp',
    };

    const promise = firstValueFrom(service.uploadStepImage(mockFile));
    const req = httpMock.expectOne('/api/media/recipe-step');
    expect(req.request.method).toBe('POST');
    req.flush(responseData);

    const result = await promise;
    expect(result.imagePath).toBe('/api/uploads/steps/step-token.webp');
  });
});
