using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Aspose.Cells;
using Aspose.Imaging;
using Aspose.Pdf;
using Aspose.Pdf.Text;
using Aspose.Slides;
using Aspose.Words;
using BitMiracle.LibTiff.Classic;
using Newtonsoft.Json;
using SkiaSharp;
using Sungero.Core;
using Sungero.Docflow.Structures.Module;

namespace Sungero.Docflow.Isolated.PdfConverter
{
  /// <summary>
  /// Базовый конвертер в pdf. Реализует общую логику конвейера преобразования в pdf для разных форматов.
  /// Специфическая логика предобработки и постобработки реализуется в классах-наследниках.
  /// </summary>
  public abstract class ConverterBase
  {
    #region Константы
    
    /// <summary>
    /// Минимальная совместимая версия PDF для корректного отображения отметки.
    /// </summary>
    public const string MinCompatibleVersion = "1.4.0.0";
    
    /// <summary>
    /// Ширина страницы, чтобы она была формата А4 (число взято из Aspose.Pdf).
    /// </summary>
    public const int PageWidth = 595;
    
    /// <summary>
    /// Высота страницы, чтобы она была формата А4 (число взято из Aspose.Pdf).
    /// </summary>
    public const int PageHeight = 842;
    
    /// <summary>
    /// Размеры левого, правого и верхнего поля страницы, чтобы она была формата А4 (число взято из Aspose.Pdf).
    /// </summary>
    public const int LeftRighTopMargin = 35;
    
    /// <summary>
    /// Размер нижнего поля страницы, чтобы она была формата А4 (число взято из Aspose.Pdf).
    /// </summary>
    public const int BottomMargin = 55;
    
    /// <summary>
    /// Имя нового экземпляра класса библиотеки LibTiff.
    /// </summary>
    public const string LibTiffInstanceName = "in-memory";
    
    /// <summary>
    /// Режим открытия файла библиотекой LibTiff.
    /// </summary>
    public const string LibTiffOpenMode = "r";
    
    #endregion
    
    #region Методы для заказной разработки
    
    /// <summary>
    /// Преобразовать изображение в pdf без подгона под а4.
    /// </summary>
    /// <param name="inputStream">Поток с картинкой.</param>
    /// <param name="resultStream">Поток для записи результата.</param>
    public virtual void ConvertImageToPdfWithoutScale(Stream inputStream, Stream resultStream)
    {

      var pdfDocument = new Aspose.Pdf.Document();
      var page = pdfDocument.Pages.Add();

      using (var img = System.Drawing.Image.FromStream(inputStream))
      {
        var imageHeight = img.Height * img.GetFrameCount(FrameDimension.Page);
        page.PageInfo.Height = imageHeight;
        page.PageInfo.Width = img.Width;
        page.PageInfo.Margin = new Aspose.Pdf.MarginInfo(0, 0, 0, 0);
        var image = new Aspose.Pdf.Image
        {
          ImageStream = inputStream,
          IsInNewPage = true,
          IsKeptWithNext = true,
          HorizontalAlignment = Aspose.Pdf.HorizontalAlignment.Center,
        };
        page.Paragraphs.Add(image);
        pdfDocument.Save(resultStream);
      }
    }
    
    /// <summary>
    /// Заполнить метаданные pdf документа.
    /// </summary>
    /// <param name="inputStream">Поток с pdf документом.</param>
    /// <param name="author">Автор документа.</param>
    /// <param name="creationDate">Дата создания документа.</param>
    /// <param name="modificationDate">Дата изменения документа.</param>
    /// <returns>Поток с документом.</returns>
    [Public]
    public virtual Stream FillPdfDocumentMetadata(Stream inputStream, string author, DateTime creationDate, DateTime modificationDate)
    {
      var resultStream = new MemoryStream();
      
      try
      {
        var document = new Aspose.Pdf.Document(inputStream);
        this.PreprocessPdfDocument(document);
        this.FillPdfDocumentMetadata(document, author, creationDate, modificationDate);
        this.SaveDocument(document, resultStream, document.PdfFormat);
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot fill pdf document metadata", ex);
        throw new AppliedCodeException("Cannot fill pdf document metadata");
      }
      
      return resultStream;
    }
    
    #endregion
    
    #region Преобразование в pdf
    
    /// <summary>
    /// Проверить, поддерживается ли формат файла по его расширению.
    /// </summary>
    /// <param name="extension">Расширение файла.</param>
    /// <returns>True/false.</returns>
    public static bool CheckIfExtensionIsSupported(string extension)
    {
      var supportedFormatList = new List<string>() { "pdf", "docx", "doc", "rtf", "xls", "xlsx", "xlsm", "odt", "ods", "txt",
        "jpg", "jpeg", "png", "bmp", "tif", "tiff", "gif", "html" };
      
      return supportedFormatList.Contains(extension.ToLower());
    }
    
    /// <summary>
    /// Преобразовать документ в pdf.
    /// </summary>
    /// <param name="inputStream">Поток с входным документом.</param>
    /// <param name="extension">Расширение входного документа.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Поток с документом.</returns>
    public virtual Stream GeneratePdf(Stream inputStream, string extension, params string[] saveParameters)
    {
      try
      {
        switch (extension.ToLower())
        {
          case "pdf":
            return this.ConvertPdfToPdf(inputStream, saveParameters);
          case "doc":
          case "docx":
          case "odt":
          case "rtf":
            return this.ConvertWordToPdf(inputStream, saveParameters);
          case "xls":
          case "xlsx":
          case "xlsm":
          case "ods":
            return this.ConvertExcelToPdf(inputStream, saveParameters);
          case "ppt":
          case "pptx":
            return this.ConvertPresentationToPdf(inputStream, saveParameters);
          case "jpg":
          case "jpeg":
          case "png":
          case "bmp":
            return this.ConvertImageToPdf(inputStream, saveParameters);
          case "tiff":
          case "tif":
            return this.ConvertScanToPdf(inputStream, saveParameters);
          case "gif":
            return this.ConvertGifToPdf(inputStream, saveParameters);
          case "txt":
            return this.ConvertTxtToPdf(inputStream, saveParameters);
          case "html":
            return this.ConvertHtmlToPdf(inputStream);
          default:
            return null;
        }
      }
      catch (Exception ex)
      {
        Logger.Error("Cannot convert file to pdf", ex);
        throw new AppliedCodeException("Cannot convert file to pdf");
      }
      finally
      {
        inputStream.Close();
      }
    }
    
    #region Преобразование pdf
    
    /// <summary>
    /// Преобразовать pdf документ в pdf.
    /// </summary>
    /// <param name="inputStream">Поток с исходным документом.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Поток для записи результата.</returns>
    public virtual Stream ConvertPdfToPdf(Stream inputStream, params string[] saveParameters)
    {
      var resultStream = new MemoryStream();
      try
      {
        var document = new Aspose.Pdf.Document(inputStream);
        this.PreprocessPdfDocument(document);
        this.Validate(document);
        var pdfFormat = this.GetPdfFormat(document, saveParameters);
        this.SaveDocument(document, resultStream, pdfFormat);
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot convert pdf", ex);
        throw new AppliedCodeException("Cannot convert pdf");
      }
      return resultStream;
    }
    
    /// <summary>
    /// Получить формат pdf.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Формат pdf.</returns>
    public virtual Aspose.Pdf.PdfFormat GetPdfFormat(Aspose.Pdf.Document document, params string[] saveParameters)
    {
      return document.PdfFormat;
    }
    
    /// <summary>
    /// Обработка pdf-документа перед преобразованием в pdf.
    /// </summary>
    /// <param name="document">Документ.</param>
    public virtual void PreprocessPdfDocument(Aspose.Pdf.Document document)
    {
      
    }
    
    /// <summary>
    /// Заполнить метаданные pdf-документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="author">Автор документа.</param>
    /// <param name="creationDate">Дата создания документа.</param>
    /// <param name="modificationDate">Дата изменения документа.</param>
    [Public]
    public virtual void FillPdfDocumentMetadata(Aspose.Pdf.Document document,
                                                string author,
                                                DateTime creationDate,
                                                DateTime modificationDate)
    {
      document.Info.Author = author;
      document.Info.CreationDate = creationDate;
      document.Info.ModDate = modificationDate;
    }
    
    /// <summary>
    /// Проверить документ на валидность перед конвертацией в pdf.
    /// </summary>
    /// <param name="document">Документ.</param>
    public virtual void Validate(Aspose.Pdf.Document document)
    {
      
    }
    
    /// <summary>
    /// Сохранить поток в pdf-документ.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="resultStream">Поток.</param>
    /// <param name="pdfFormat">Формат pdf.</param>
    public virtual void SaveDocument(Aspose.Pdf.Document document,
                                     MemoryStream resultStream,
                                     Aspose.Pdf.PdfFormat pdfFormat)
    {
      document.Save(resultStream);
    }
    
    #endregion
    
    #region Преобразование текстовых документов с форматированием
    
    /// <summary>
    /// Преобразовать текстовый документ в pdf.
    /// </summary>
    /// <param name="inputStream">Поток с текстовым документом.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Поток для записи результата.</returns>
    public virtual Stream ConvertWordToPdf(Stream inputStream, params string[] saveParameters)
    {
      var resultStream = new MemoryStream();
      try
      {
        var word = new Aspose.Words.Document(inputStream);
        this.PreprocessWordDocument(word);
        this.Validate(word);
        var saveOptions = this.GetWordSaveOptions(word, saveParameters);
        word.Save(resultStream, saveOptions);
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot convert word to pdf", ex);
        throw new AppliedCodeException("Cannot convert word to pdf");
      }
      return resultStream;
    }
    
    /// <summary>
    /// Обработка текстового документа перед конвертацией в pdf.
    /// </summary>
    /// <param name="word">Документ.</param>
    public virtual void PreprocessWordDocument(Aspose.Words.Document word)
    {
      
    }
    
    /// <summary>
    /// Получить опции сохранения для текстового документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Опции сохранения для текстового документа.</returns>
    public virtual Aspose.Words.Saving.SaveOptions GetWordSaveOptions(Aspose.Words.Document document, params string[] saveParameters)
    {
      var saveOptions = Aspose.Words.Saving.SaveOptions.CreateSaveOptions(Aspose.Words.SaveFormat.Pdf);
      // При обновлении вычисляемых полей они начинают отображаться на английском. Поэтому автообновление нужно отключить (142678).
      saveOptions.UpdateFields = false;
      return saveOptions;
    }
    
    /// <summary>
    /// Проверить документ на валидность перед конвертацией в pdf.
    /// </summary>
    /// <param name="word">Документ.</param>
    public virtual void Validate(Aspose.Words.Document word)
    {
      
    }
    
    #endregion
    
    #region Преобразование таблиц
    
    /// <summary>
    /// Преобразовать таблицы excel в pdf.
    /// </summary>
    /// <param name="inputStream">Поток с документом-таблицей.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Поток для записи результата.</returns>
    public virtual Stream ConvertExcelToPdf(Stream inputStream, params string[] saveParameters)
    {
      var resultStream = new MemoryStream();
      try
      {
        var workbook = new Aspose.Cells.Workbook(inputStream);
        var saveOptions = this.GetCellsSaveOptions(workbook, saveParameters);
        this.Validate(workbook);
        workbook.Save(resultStream, saveOptions);
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot convert excel to pdf", ex);
        throw new AppliedCodeException("Cannot convert excel to pdf");
      }
      return resultStream;
    }
    
    /// <summary>
    /// Получить опции сохранения для таблицы.
    /// </summary>
    /// <param name="workbook">Документ.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Опции сохранения для таблицы.</returns>
    public virtual Aspose.Cells.PdfSaveOptions GetCellsSaveOptions(Aspose.Cells.Workbook workbook, params string[] saveParameters)
    {
      var saveOptions = new Aspose.Cells.PdfSaveOptions(Aspose.Cells.SaveFormat.Pdf);
      if (workbook.FileFormat == FileFormatType.Ods)
        saveOptions.AllColumnsInOnePagePerSheet = true;
      return saveOptions;
    }
    
    /// <summary>
    /// Проверить документ на валидность перед конвертацией в pdf.
    /// </summary>
    /// <param name="workbook">Документ.</param>
    public virtual void Validate(Aspose.Cells.Workbook workbook)
    {
      
    }
    
    #endregion
    
    #region Преобразование презентаций
    
    /// <summary>
    /// Преобразовать презентацию в pdf.
    /// </summary>
    /// <param name="inputStream">Поток с презентацией.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Поток для записи результата.</returns>
    public virtual Stream ConvertPresentationToPdf(Stream inputStream, params string[] saveParameters)
    {
      var resultStream = new MemoryStream();
      try
      {
        var presentation = new Aspose.Slides.Presentation(inputStream);
        var saveOptions = this.GetSlidesSaveOptions(presentation, saveParameters);
        this.Validate(presentation);
        presentation.Save(resultStream, Aspose.Slides.Export.SaveFormat.Pdf, saveOptions);
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot convert presentation to pdf", ex);
        throw new AppliedCodeException("Cannot convert presentation to pdf");
      }
      return resultStream;
    }
    
    /// <summary>
    /// Получить опции сохранения для презентации.
    /// </summary>
    /// <param name="presentation">Презентация.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Опции сохранения для презентации.</returns>
    public virtual Aspose.Slides.Export.PdfOptions GetSlidesSaveOptions(Aspose.Slides.Presentation presentation, params string[] saveParameters)
    {
      var saveOptions = new Aspose.Slides.Export.PdfOptions();
      return saveOptions;
    }
    
    /// <summary>
    /// Проверить документ на валидность перед конвертацией в pdf.
    /// </summary>
    /// <param name="presentation">Документ.</param>
    public virtual void Validate(Aspose.Slides.Presentation presentation)
    {
      
    }
    
    #endregion
    
    #region Преобразование изображений
    
    /// <summary>
    /// Преобразовать изображение в pdf.
    /// </summary>
    /// <param name="inputStream">Поток с картинкой.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Поток для записи результата.</returns>
    public virtual Stream ConvertImageToPdf(Stream inputStream, params string[] saveParameters)
    {
      var resultStream = new MemoryStream();
      try
      {
        var document = new Aspose.Words.Document();
        var builder = this.GetDocumentBuilder(document);
        this.AddImage(inputStream, builder);
        var options = this.GetImageSaveOptions(document, saveParameters);
        document.Save(resultStream, options);
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot convert image to pdf", ex);
        throw new AppliedCodeException("Cannot convert image to pdf");
      }
      return resultStream;
    }
    
    /// <summary>
    /// Получить обработчик документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Обработчик документа.</returns>
    public virtual Aspose.Words.DocumentBuilder GetDocumentBuilder(Aspose.Words.Document document)
    {
      var builder = new DocumentBuilder(document);
      builder.PageSetup.PageWidth = PageWidth;
      builder.PageSetup.PageHeight = PageHeight;
      builder.PageSetup.LeftMargin = builder.PageSetup.RightMargin = builder.PageSetup.TopMargin = LeftRighTopMargin;
      builder.PageSetup.BottomMargin = BottomMargin;
      return builder;
    }
    
    /// <summary>
    /// Добавить картинку в документ.
    /// </summary>
    /// <param name="inputStream">Поток с документом.</param>
    /// <param name="builder">Обработчик документа.</param>
    public virtual void AddImage(Stream inputStream, Aspose.Words.DocumentBuilder builder)
    {
      inputStream.Flush();
      inputStream.Position = 0;
      using (var image = Aspose.Imaging.Image.Load(inputStream))
      {
        var pageSize = this.CalculatePageSize(image.Width, image.Height, builder);
        
        using (var memoryStream = new MemoryStream())
        {
          var exportOptions = new Aspose.Imaging.ImageOptions.PngOptions();
          image.Save(memoryStream, exportOptions);
          var bytes = memoryStream.ToArray();
          builder.InsertImage(bytes, pageSize.Width, pageSize.Height);
        }
      }
    }
    
    /// <summary>
    /// Рассчитать размеры страницы.
    /// </summary>
    /// <param name="imageWidth">Ширина картинки.</param>
    /// <param name="imageHeight">Высота картинки.</param>
    /// <param name="builder">Обработчик документа.</param>
    /// <returns>Масштабированные размеры страницы.</returns>
    public virtual ScaledPageSize CalculatePageSize(double imageWidth, double imageHeight, Aspose.Words.DocumentBuilder builder)
    {
      var resultedPageSize = new ScaledPageSize();
      builder.PageSetup.Orientation = imageWidth > imageHeight ? Aspose.Words.Orientation.Landscape : Aspose.Words.Orientation.Portrait;
      var maxHeight = builder.PageSetup.PageHeight - builder.PageSetup.TopMargin - builder.PageSetup.BottomMargin;
      var maxWidth = builder.PageSetup.PageWidth - builder.PageSetup.LeftMargin - builder.PageSetup.RightMargin;
      var ratio = Math.Min(maxWidth / imageWidth, maxHeight / imageHeight);
      resultedPageSize.Width = imageWidth * ratio;
      resultedPageSize.Height = imageHeight * ratio;
      return resultedPageSize;
    }
    
    /// <summary>
    /// Получить опции сохранения для картинки.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Опции сохранения для картинки.</returns>
    public virtual Aspose.Words.Saving.PdfSaveOptions GetImageSaveOptions(Aspose.Words.Document document, params string[] saveParameters)
    {
      var saveOptions = (Aspose.Words.Saving.PdfSaveOptions)Aspose.Words.Saving.PdfSaveOptions.CreateSaveOptions(Aspose.Words.SaveFormat.Pdf);
      // Отключить понижение качества вставляемых картинок.
      saveOptions.DownsampleOptions.DownsampleImages = false;
      return saveOptions;
    }
    
    #endregion
    
    #region Преобразование сканов

    /// <summary>
    /// Преобразовать изображение tiff/tif в pdf.
    /// </summary>
    /// <param name="inputStream">Поток с документом формата tiff/tif.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Поток для записи результата.</returns>
    public virtual Stream ConvertScanToPdf(Stream inputStream, params string[] saveParameters)
    {
      var resultStream = new MemoryStream();
      try
      {
        var document = new Aspose.Words.Document();
        var builder = this.GetDocumentBuilder(document);
        var convertedInPngScans = this.ConvertScanToPngs(inputStream);
        this.AddImages(builder, convertedInPngScans);
        var saveOptions = this.GetImageSaveOptions(document, saveParameters);
        document.Save(resultStream, saveOptions);
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot convert tiff to pdf", ex);
        throw new AppliedCodeException("Cannot convert tiff to pdf");
      }
      
      return resultStream;
    }
    
    /// <summary>
    /// Добавить картинки в документ.
    /// </summary>
    /// <param name="builder">Обработчик документа.</param>
    /// <param name="inputStream">Поток с документом.</param>
    /// <remarks>Во входящем потоке с документом - несколько картинок формата tiff/tif.</remarks>
    public virtual void AddImages(DocumentBuilder builder, Stream inputStream)
    {
      using (var image = System.Drawing.Image.FromStream(inputStream))
      {
        var dimension = new FrameDimension(image.FrameDimensionsList[0]);
        var framesCount = image.GetFrameCount(dimension);
        for (int frameIdx = 0; frameIdx < framesCount; frameIdx++)
        {
          if (frameIdx != 0)
            builder.InsertBreak(BreakType.SectionBreakNewPage);
          image.SelectActiveFrame(dimension, frameIdx);
          var pageSize = this.CalculatePageSize(image.Width, image.Height, builder);
          
          using (var memoryStream = new MemoryStream())
          {
            image.Save(memoryStream, ImageFormat.Png);
            var bytes = memoryStream.ToArray();
            builder.InsertImage(bytes, pageSize.Width, pageSize.Height);
          }
        }
      }
    }
    
    /// <summary>
    /// Добавить картинки в документ.
    /// </summary>
    /// <param name="builder">Обработчик документа.</param>
    /// <param name="inputStreams">Список потоков со страницами документа.</param>
    /// <remarks>Во входящих потоках - страницы документа в формате png (по одной странице в каждом потоке).
    /// Перегрузка добавлена для корректного преобразования tiff/tif в pdf на linux с использованием библиотеки LibTiff.</remarks>
    public virtual void AddImages(DocumentBuilder builder, List<MemoryStream> inputStreams)
    {
      for (int frameIdx = 0; frameIdx < inputStreams.Count; frameIdx++)
      {
        using (var image = System.Drawing.Image.FromStream(inputStreams[frameIdx]))
        {
          if (frameIdx != 0)
            builder.InsertBreak(BreakType.SectionBreakNewPage);
          var pageSize = this.CalculatePageSize(image.Width, image.Height, builder);
          
          using (var memoryStream = new MemoryStream())
          {
            image.Save(memoryStream, ImageFormat.Png);
            var bytes = memoryStream.ToArray();
            builder.InsertImage(bytes, pageSize.Width, pageSize.Height);
          }
        }
      }
    }
    
    /// <summary>
    /// Конвертация tiff/tif в список потоков изображений png.
    /// </summary>
    /// <param name="inputStream">Поток с документом формата tiff/tif.</param>
    /// <returns>Список потоков, содержащих изображения png.</returns>
    public virtual List<MemoryStream> ConvertScanToPngs(Stream inputStream)
    {
      var resultStreams = new List<MemoryStream>();
      var tiff = Tiff.ClientOpen(LibTiffInstanceName, LibTiffOpenMode, inputStream, new TiffStream());
      short directoriesCount = tiff.NumberOfDirectories();
      for (short dirIdx = 0; dirIdx < directoriesCount; dirIdx++)
      {
        tiff.SetDirectory(dirIdx);
        FieldValue[] value = tiff.GetField(TiffTag.IMAGEWIDTH);
        int width = value[0].ToInt();
        value = tiff.GetField(TiffTag.IMAGELENGTH);
        int height = value[0].ToInt();
        int[] raster = new int[height * width];
        
        if (!tiff.ReadRGBAImage(width, height, raster))
          throw new AppliedCodeException("Cannot read the image and decode it into RGBA format raster.");
        
        resultStreams.Add(this.ConvertDecodedRasterToPng(raster, width, height));
      }
      
      return resultStreams;
    }
    
    /// <summary>
    /// Конвертировать декодированные данные в поток данных изображения.
    /// </summary>
    /// <param name="raster">Декодированные данные.</param>
    /// <param name="width">Ширина изображения.</param>
    /// <param name="height">Высота изображения.</param>
    /// <returns>Поток сконвертированного изображения.</returns>
    public virtual MemoryStream ConvertDecodedRasterToPng(int[] raster, int width, int height)
    {
      using (Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
      {
        var rectangle = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData bmpdata = bmp.LockBits(rectangle, ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        byte[] bits = new byte[bmpdata.Stride * bmpdata.Height];
        
        for (int y = 0; y < bmp.Height; y++)
        {
          int rasterOffset = y * bmp.Width;
          int bitsOffset = (bmp.Height - y - 1) * bmpdata.Stride;
          for (int x = 0; x < bmp.Width; x++)
          {
            int rgba = raster[rasterOffset++];
            bits[bitsOffset++] = (byte)((rgba >> 16) & 0xff);
            bits[bitsOffset++] = (byte)((rgba >> 8) & 0xff);
            bits[bitsOffset++] = (byte)(rgba & 0xff);
            bits[bitsOffset++] = (byte)((rgba >> 24) & 0xff);
          }
        }
        
        Marshal.Copy(bits, 0, bmpdata.Scan0, bits.Length);
        bmp.UnlockBits(bmpdata);
        var memoryStream = new MemoryStream();
        var encoder = this.GetEncoder("image/png");
        var encoderParams = this.GetEncoderParameters(EncoderValue.CompressionLZW);
        bmp.Save(memoryStream, encoder, encoderParams);
        encoderParams.Dispose();
        
        return memoryStream;
      }
    }
    
    #endregion
    
    #region Преобразование картинок формата gif
    
    /// <summary>
    /// Преобразовать gif в pdf.
    /// </summary>
    /// <param name="inputStream">Поток с документом формата gif.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Поток для записи результата.</returns>
    public virtual Stream ConvertGifToPdf(Stream inputStream, params string[] saveParameters)
    {
      var resultStream = new MemoryStream();
      try
      {
        var doc = new Aspose.Words.Document();
        var builder = this.GetDocumentBuilder(doc);
        this.AddGif(inputStream, builder);
        var saveOptions = this.GetImageSaveOptions(doc, saveParameters);
        doc.Save(resultStream, saveOptions);
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot convert gif to pdf", ex);
        throw new AppliedCodeException("Cannot convert gif to pdf");
      }
      return resultStream;
    }
    
    /// <summary>
    /// Вставить в документ картинку формата gif.
    /// </summary>
    /// <param name="inputStream">Поток памяти с картинкой.</param>
    /// <param name="builder">Обработчик документа.</param>
    public virtual void AddGif(Stream inputStream, Aspose.Words.DocumentBuilder builder)
    {
      inputStream.Flush();
      inputStream.Position = 0;
      using (var skiaSharpStream = new SKManagedStream(inputStream))
        using (var codec = SKCodec.Create(skiaSharpStream))
      {
        int frameCount = codec.FrameCount;
        for (int frame = 0; frame < frameCount; frame++)
        {
          SKImageInfo imageInfo = new SKImageInfo(codec.Info.Width, codec.Info.Height);
          var bitmap = new SKBitmap(imageInfo);
          IntPtr pointer = bitmap.GetPixels();
          SKCodecOptions codecOptions = new SKCodecOptions(frame);
          codec.GetPixels(imageInfo, pointer, codecOptions);

          using (SKImage image = SKImage.FromBitmap(bitmap))
          {
            using (SKData data = image.Encode())
            {
              byte[] imageByteArray = data.ToArray();
              if (frame != 0)
                builder.InsertBreak(BreakType.SectionBreakNewPage);
              builder.InsertImage(imageByteArray);
            }
          }
        }
      }
    }
    
    #endregion
    
    #region Преобразование текстовых документов
    
    /// <summary>
    /// Преобразовать txt в pdf.
    /// </summary>
    /// <param name="inputStream">Поток с документом формата txt.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Поток для записи результата.</returns>
    /// <remarks>Преобразование происходит в две попытки. Если не смогли сохранить документ в pdf, используя word,
    /// то создаем новый pdf-документ и копируем в него текст из исходного документа.</remarks>
    public virtual Stream ConvertTxtToPdf(Stream inputStream, params string[] saveParameters)
    {
      var resultStream = new MemoryStream();
      try
      {
        var wordDocument = new Aspose.Words.Document(inputStream);
        var saveOptions = this.GetWordSaveOptions(wordDocument, saveParameters);
        wordDocument.Save(resultStream, saveOptions);
      }
      catch (Aspose.Words.UnsupportedFileFormatException ex)
      {
        Logger.Debug("Cannot txt to pdf via Aspose.Words. Document converted via Aspose.Pdf", ex);
        
        // Преобразовать, используя Aspose.Pdf.
        // Aspose.Words мог не распознать формат txt содержащим только пробелы.
        // Aspose.Pdf преобразует, не соблюдая разбивку по страницам и форматирование пробелами.
        var pdfDocument = new Aspose.Pdf.Document();
        this.AddTextToPdfDocument(inputStream, pdfDocument);
        pdfDocument.Save(resultStream);
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot convert txt to pdf", ex);
        throw new AppliedCodeException("Cannot convert txt to pdf");
      }
      return resultStream;
    }
    
    /// <summary>
    /// Добавить текст в pdf-документ.
    /// </summary>
    /// <param name="inputStream">Поток памяти с текстом.</param>
    /// <param name="pdfDocument">Pdf-документ.</param>
    public void AddTextToPdfDocument(Stream inputStream, Aspose.Pdf.Document pdfDocument)
    {
      var page = pdfDocument.Pages.Add();
      using (TextReader textReader = new StreamReader(inputStream))
      {
        var text = new TextFragment(textReader.ReadToEnd());
        page.Paragraphs.Add(text);
      }
    }
    
    #endregion
    
    /// <summary>
    /// Преобразовать html в pdf.
    /// </summary>
    /// <param name="inputStream">Поток с документом формата html.</param>
    /// <returns>Поток для записи результата.</returns>
    public virtual Stream ConvertHtmlToPdf(Stream inputStream)
    {
      var resultStream = new MemoryStream();
      try
      {
        var htmlDocument = new Aspose.Words.Document(inputStream, new Aspose.Words.Loading.HtmlLoadOptions());
        htmlDocument.Save(resultStream, Aspose.Words.SaveFormat.Pdf);
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot convert html to pdf", ex);
        throw new AppliedCodeException("Cannot convert html to pdf");
      }
      return resultStream;
    }
    
    /// <summary>
    /// Получить информацию о кодировке и декодировке изображения.
    /// </summary>
    /// <param name="mimeType">Строка, содержащая тип MIME.</param>
    /// <returns>Информация о кодировке и декодировке изображения.</returns>
    public virtual ImageCodecInfo GetEncoder(string mimeType)
    {
      return ImageCodecInfo.GetImageEncoders()
        .FirstOrDefault(t => string.Equals(t.MimeType, mimeType, StringComparison.OrdinalIgnoreCase));
    }
    
    /// <summary>
    /// Получить параметры кодировщика.
    /// </summary>
    /// <param name="compression">Метод сжатия изображения.</param>
    /// <returns>Параметры кодировщика.</returns>
    public virtual EncoderParameters GetEncoderParameters(EncoderValue compression)
    {
      var countParameters = 1;
      var encoderParams = new EncoderParameters(countParameters);
      encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Compression, (long)compression);
      return encoderParams;
    }
    
    #endregion
  }
  
  /// <summary>
  /// Конвертер в pdf. Реализует специфичную логику предобработки и постобработки тела документа при преобразовании в pdf.
  /// </summary>
  public class Converter : ConverterBase
  {
    #region Преобразование в pdf
    
    public override void PreprocessPdfDocument(Aspose.Pdf.Document document)
    {
      if (document.IsPdfaCompliant)
        document.RemovePdfaCompliance();
    }
    
    /// <summary>
    /// Обработка текстового документа перед конвертацией в pdf.
    /// </summary>
    /// <param name="word">Документ.</param>
    public override void PreprocessWordDocument(Aspose.Words.Document word)
    {
      word.AcceptAllRevisions();
      var comments = word.GetChildNodes(NodeType.Comment, true);
      comments.Clear();
    }
    
    #endregion
    
    #region Проверка наличия текстового слоя
    
    /// <summary>
    /// Проверить, есть ли в документе текстовый слой.
    /// </summary>
    /// <param name="inputStream">Поток с входным документом формата PDF.</param>
    /// <returns>True, если есть, иначе - false.</returns>
    /// <remarks>Наличие текста проверяется только на первой странице.</remarks>
    public virtual bool CheckDocumentTextLayer(Stream inputStream)
    {
      try
      {
        var document = new Aspose.Pdf.Document(inputStream);
        var textSelector = new Aspose.Pdf.OperatorSelector(new Aspose.Pdf.Operators.TextShowOperator());
        document.Pages[1].Contents.Accept(textSelector);
        return textSelector.Selected.Count != 0;
      }
      catch (Exception ex)
      {
        Logger.Error("Cannot check document has text layer", ex);
        throw new AppliedCodeException("Cannot check document has text layer");
      }
      finally
      {
        inputStream.Close();
      }
    }

    #endregion
  }
  
  /// <summary>
  /// Конвертер в pdf/a. Реализует логику предобработки тела документа и сохранение в pdf/a.
  /// </summary>
  public class ConverterPdfA : ConverterBase
  {
    #region Константы
    
    public const double MarginForImages = 0.0;
    
    #endregion
    
    #region Поля и свойства
    
    /// <summary>
    /// Список ошибок при встраивании шрифтов в pdf.
    /// </summary>
    /// <value>По умолчанию пустой список. На событии замены шрифта добавляется текст ошибки.</value>
    public List<string> FontSubstitutionErrors { get; private set; }

    #endregion
    
    #region Преобразование в pdf/a
    
    #region Преобразование pdf
    
    public override void PreprocessPdfDocument(Aspose.Pdf.Document document)
    {
      this.EmbedFonts(document);
    }
    
    /// <summary>
    /// Встроить шрифты.
    /// </summary>
    /// <param name="document">Доумент.</param>
    public virtual void EmbedFonts(Aspose.Pdf.Document document)
    {
      document.EmbedStandardFonts = true;
      var fonts = document.FontUtilities.GetAllFonts();
      foreach (var font in fonts)
      {
        if (font.IsAccessible && !font.IsEmbedded)
          font.IsEmbedded = true;
        else if (!font.IsAccessible && !font.IsEmbedded && !font.IsSubset)
          throw new AppliedCodeException(string.Format("Font {0} not found", font.FontName));
      }
      
      this.SetSubstitutionHandler(document);
      document.Save();
    }
    
    /// <summary>
    /// Установить обработчик события замены шрифтов при конвертации документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    public virtual void SetSubstitutionHandler(Aspose.Pdf.Document document)
    {
      this.FontSubstitutionErrors = new List<string>();
      Aspose.Pdf.Document.FontSubstitutionHandler substitutionHandler = new Aspose.Pdf.Document.FontSubstitutionHandler(this.OnFontSubstitution);
      document.FontSubstitution += substitutionHandler;
    }
    
    /// <summary>
    /// Обработчик события замены шрифта.
    /// </summary>
    /// <param name="oldFont">Старый шрифт.</param>
    /// <param name="newFont">Новый шрифт.</param>
    public virtual void OnFontSubstitution(Aspose.Pdf.Text.Font oldFont, Aspose.Pdf.Text.Font newFont)
    {
      this.FontSubstitutionErrors.Add(string.Format("Замена шрифта не допускается. Попытка заменить шрифт '{0}' на '{1}'", (object)oldFont.FontName, (object)newFont.FontName));
    }
    
    /// <summary>
    /// Сохранить поток в pdf-документ.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="resultStream">Поток.</param>
    /// <param name="pdfFormat">Формат pdf.</param>
    public override void SaveDocument(Aspose.Pdf.Document document,
                                      MemoryStream resultStream,
                                      Aspose.Pdf.PdfFormat pdfFormat)
    {
      using (var memoryStream = new MemoryStream())
      {
        document.Convert(memoryStream, pdfFormat, ConvertErrorAction.Delete);
        document.Save(resultStream);
      }
    }
    
    #endregion
    
    #region Преобразование текстовых документов с форматированием
    
    /// <summary>
    /// Получить опции сохранения для текстового документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Опции сохранения для текстового документа.</returns>
    public override Aspose.Words.Saving.SaveOptions GetWordSaveOptions(Aspose.Words.Document document, params string[] saveParameters)
    {
      var saveOptions = (Aspose.Words.Saving.PdfSaveOptions)base.GetWordSaveOptions(document);
      switch (saveParameters[0])
      {
        case "v1A":
          saveOptions.Compliance = Aspose.Words.Saving.PdfCompliance.PdfA1a;
          break;
        case "v1B":
          saveOptions.Compliance = Aspose.Words.Saving.PdfCompliance.PdfA1b;
          break;
        default:
          throw new AppliedCodeException(string.Format("Converting to {0} not supported for Word", saveParameters));
      }
      return saveOptions;
    }
    
    /// <summary>
    /// Проверить документ на валидность перед конвертацией в pdf.
    /// </summary>
    /// <param name="word">Документ.</param>
    public override void Validate(Aspose.Words.Document word)
    {
      var fonts = word.FontInfos;
      var fontNames = fonts.Select(x => x.Name).ToList();
      this.CheckFonts(fontNames);
    }
    
    #endregion
    
    #region Преобразование таблиц
    
    /// <summary>
    /// Получить опции сохранения для таблицы.
    /// </summary>
    /// <param name="workbook">Документ.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Опции сохранения для таблицы.</returns>
    public override Aspose.Cells.PdfSaveOptions GetCellsSaveOptions(Aspose.Cells.Workbook workbook, params string[] saveParameters)
    {
      var saveOptions = (Aspose.Cells.PdfSaveOptions)base.GetCellsSaveOptions(workbook);
      switch (saveParameters[0])
      {
        case "v1A":
          saveOptions.Compliance = Aspose.Cells.Rendering.PdfCompliance.PdfA1a;
          break;
        case "v1B":
          saveOptions.Compliance = Aspose.Cells.Rendering.PdfCompliance.PdfA1b;
          break;
        default:
          throw new AppliedCodeException(string.Format("Convert to {0} not supported for Excel", saveParameters));
      }
      return saveOptions;
    }
    
    /// <summary>
    /// Проверить документ на валидность перед конвертацией в pdf.
    /// </summary>
    /// <param name="workbook">Документ.</param>
    public override void Validate(Aspose.Cells.Workbook workbook)
    {
      var fonts = workbook.GetFonts();
      var fontNames = fonts.Select(x => x.Name).ToList();
      this.CheckFonts(fontNames);
    }
    
    #endregion
    
    #region Преобразование презентаций
    
    /// <summary>
    /// Получить опции сохранения для презентации.
    /// </summary>
    /// <param name="presentation">Презентация.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Опции сохранения для презентации.</returns>
    public override Aspose.Slides.Export.PdfOptions GetSlidesSaveOptions(Aspose.Slides.Presentation presentation, params string[] saveParameters)
    {
      var saveOptions = (Aspose.Slides.Export.PdfOptions)base.GetSlidesSaveOptions(presentation);
      switch (saveParameters[0])
      {
        case "v1A":
          saveOptions.Compliance = Aspose.Slides.Export.PdfCompliance.PdfA1a;
          break;
        case "v1B":
          saveOptions.Compliance = Aspose.Slides.Export.PdfCompliance.PdfA1b;
          break;
        default:
          throw new AppliedCodeException(string.Format("Convert to {0} not supported for presentation", saveParameters));
      }
      return saveOptions;
    }
    
    /// <summary>
    /// Проверить документ на валидность перед конвертацией в pdf.
    /// </summary>
    /// <param name="presentation">Документ.</param>
    public override void Validate(Aspose.Slides.Presentation presentation)
    {
      var fonts = presentation.FontsManager.GetFonts();
      var fontNames = fonts.Select(x => x.FontName).ToList();
      this.CheckFonts(fontNames);
    }
    
    #endregion
    
    #region Преобразование изображений
    
    /// <summary>
    /// Получить опции сохранения для картинки.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Опции сохранения для картинки.</returns>
    public override Aspose.Words.Saving.PdfSaveOptions GetImageSaveOptions(Aspose.Words.Document document, params string[] saveParameters)
    {
      var saveOptions = (Aspose.Words.Saving.PdfSaveOptions)base.GetImageSaveOptions(document);
      switch (saveParameters[0])
      {
        case "v1A":
          saveOptions.Compliance = Aspose.Words.Saving.PdfCompliance.PdfA1a;
          break;
        case "v1B":
          saveOptions.Compliance = Aspose.Words.Saving.PdfCompliance.PdfA1b;
          break;
        default:
          throw new AppliedCodeException(string.Format("Convert to {0} not supported for images", saveParameters));
      }
      return saveOptions;
    }
    
    /// <summary>
    /// Рассчитать размеры страницы.
    /// </summary>
    /// <param name="imageWidth">Ширина картинки.</param>
    /// <param name="imageHeight">Высота картинки.</param>
    /// <param name="builder">Обработчик документа.</param>
    /// <returns>Масштабированные размеры страницы.</returns>
    public override ScaledPageSize CalculatePageSize(double imageWidth, double imageHeight, DocumentBuilder builder)
    {
      var resultedPageSize = new ScaledPageSize();
      builder.PageSetup.Orientation = imageWidth > imageHeight ? Aspose.Words.Orientation.Landscape : Aspose.Words.Orientation.Portrait;
      builder.PageSetup.PageWidth = imageWidth;
      builder.PageSetup.PageHeight = imageHeight;
      resultedPageSize.Width = imageWidth;
      resultedPageSize.Height = imageHeight;
      return resultedPageSize;
    }
    
    /// <summary>
    /// Получить обработчик документа.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>Обработчик документа.</returns>
    public override DocumentBuilder GetDocumentBuilder(Aspose.Words.Document document)
    {
      var builder = new DocumentBuilder(document);
      builder.PageSetup.LeftMargin = MarginForImages;
      builder.PageSetup.RightMargin = MarginForImages;
      builder.PageSetup.TopMargin = MarginForImages;
      builder.PageSetup.BottomMargin = MarginForImages;
      return builder;
    }
    
    #endregion
    
    #region Преобразование сканов
    
    /// <summary>
    /// Преобразовать изображение tiff/tif в pdf/a.
    /// </summary>
    /// <param name="inputStream">Поток с документом формата tiff/tif.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Поток для записи результата.</returns>
    public override Stream ConvertScanToPdf(Stream inputStream, params string[] saveParameters)
    {
      var resultStream = new MemoryStream();
      var tempFrameStreamStorage = new List<MemoryStream>();
      try
      {
        var document = new Aspose.Pdf.Document();
        this.AddPages(document, inputStream, tempFrameStreamStorage);
        var pdfFormat = this.GetPdfFormat(document, saveParameters);
        
        using (var convertLog = new MemoryStream())
        {
          document.Convert(convertLog, pdfFormat, ConvertErrorAction.Delete);
          document.Save(resultStream);
        }
      }
      catch (Exception ex)
      {
        resultStream.Close();
        Logger.Error("Cannot convert tiff to pdf/a", ex);
        throw new AppliedCodeException("Cannot convert tiff to pdf/a");
      }
      finally
      {
        tempFrameStreamStorage.ForEach(x => x.Dispose());
      }
      return resultStream;
    }
    
    /// <summary>
    /// Добавить страницы из документа формата tif/tiff при конвертации.
    /// </summary>
    /// <param name="pdfDocument">Документ.</param>
    /// <param name="imageStream">Поток с документом.</param>
    /// <param name="tempFrameStreamStorage">Временное хранилище для потоков с изображениями.</param>
    public virtual void AddPages(Aspose.Pdf.Document pdfDocument, Stream imageStream, List<MemoryStream> tempFrameStreamStorage)
    {
      using (var bitmap = new System.Drawing.Bitmap(imageStream))
      {
        var dimension = new FrameDimension(bitmap.FrameDimensionsList.First());
        int frameCount = bitmap.GetFrameCount(dimension);

        var encoder = this.GetEncoder("image/png");
        var encoderParams = this.GetEncoderParameters(EncoderValue.CompressionLZW);

        for (int frameIdx = 0; frameIdx <= frameCount - 1; frameIdx++)
        {
          var currentFrameStream = new MemoryStream();
          tempFrameStreamStorage.Add(currentFrameStream);

          bitmap.SelectActiveFrame(dimension, frameIdx);
          bitmap.Save(currentFrameStream, encoder, encoderParams);

          var page = pdfDocument.Pages.Add();
          this.AddImageToPage(page, currentFrameStream);
        }
      }
    }
    
    /// <summary>
    /// Добавить изображение на страницу при конвертации в pdf/a.
    /// </summary>
    /// <param name="documentPage">Страница документа.</param>
    /// <param name="imageStream">Поток с изображением.</param>
    public virtual void AddImageToPage(Page documentPage, Stream imageStream)
    {
      using (System.Drawing.Image imageFromStream = System.Drawing.Image.FromStream(imageStream))
      {
        Aspose.Pdf.Image resultImage = new Aspose.Pdf.Image();
        resultImage.ImageStream = imageStream;
        resultImage.IsInNewPage = true;
        resultImage.IsKeptWithNext = true;
        resultImage.HorizontalAlignment = HorizontalAlignment.Center;
        Aspose.Pdf.BaseParagraph paragraph = resultImage;
        documentPage.PageInfo.Height = (double)imageFromStream.Height;
        documentPage.PageInfo.Width = (double)imageFromStream.Width;
        documentPage.PageInfo.Margin = new MarginInfo(0.0, 0.0, 0.0, 0.0);
        documentPage.Paragraphs.Add(paragraph);
      }
    }
    
    /// <summary>
    /// Получить формат pdf.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="saveParameters">Опции сохранения.</param>
    /// <returns>Формат pdf.</returns>
    public override Aspose.Pdf.PdfFormat GetPdfFormat(Aspose.Pdf.Document document, params string[] saveParameters)
    {
      var pdfFormat = new Aspose.Pdf.PdfFormat();
      switch (saveParameters[0])
      {
        case "v1A":
          pdfFormat = Aspose.Pdf.PdfFormat.PDF_A_1A;
          break;
        case "v1B":
          pdfFormat = Aspose.Pdf.PdfFormat.PDF_A_1B;
          break;
        default:
          throw new AppliedCodeException(string.Format("Convert to {0} not supported for images", saveParameters));
      }
      return pdfFormat;
    }
    
    #endregion
    
    /// <summary>
    /// Проверить доступность шрифтов.
    /// </summary>
    /// <param name="fontNames">Список шрифтов.</param>
    public virtual void CheckFonts(List<string> fontNames)
    {
      foreach (var name in fontNames)
      {
        if (!this.IsFontAvailable(name))
        {
          Logger.DebugFormat("Font {0} not found", name);
          throw new AppliedCodeException(string.Format("Font {0} not found", name));
        }
      }
    }
    
    /// <summary>
    /// Проверить доступность шрифта.
    /// </summary>
    /// <param name="fontName">Шрифт.</param>
    /// <returns>True - если шрифт доступен, иначе false.</returns>
    private bool IsFontAvailable(string fontName)
    {
      try
      {
        var font = Aspose.Pdf.Text.FontRepository.FindFont(fontName);
        if (font == null)
          return false;
        
        if (!font.IsAccessible && !font.IsEmbedded && !font.IsSubset)
          return false;
      }
      catch (Aspose.Pdf.FontNotFoundException)
      {
        return false;
      }
      return true;
    }

    #endregion

  }
  
  /// <summary>
  /// Класс простановки штампов в pdf. Реализует логику генерации и простановки штампов, а также поиска мест для вставки штампов.
  /// </summary>
  public class PdfStamper
  {
    #region Константы
    
    /// <summary>
    /// Разрешение штампа.
    /// </summary>
    public const double DotsPerCm = 72 / 2.54;
    
    /// <summary>
    /// Отступ снизу страницы для простановки штампа по умолчанию.
    /// </summary>
    public const int BottomIndent = 20;
    
    /// <summary>
    /// Минимальная совместимая версия PDF для корректного отображения отметки.
    /// </summary>
    public const string MinCompatibleVersion = "1.4.0.0";
    
    #endregion
    
    #region Отметки для вставки в pdf
    
    /// <summary>
    /// Создать штамп из шаблона html для вставки в pdf.
    /// </summary>
    /// <param name="html">Шаблон штампа в html.</param>
    /// <returns>Документ pdf со штампом.</returns>
    public virtual Aspose.Pdf.PdfPageStamp CreateStampFromHtml(string html)
    {
      try
      {
        Aspose.Pdf.HtmlLoadOptions objLoadOptions = new Aspose.Pdf.HtmlLoadOptions();
        objLoadOptions.PageInfo.Margin = new Aspose.Pdf.MarginInfo(0, 0, 0, 0);
        Aspose.Pdf.Document stampDoc;
        using (var htmlStamp = new MemoryStream(Encoding.UTF8.GetBytes(html)))
          stampDoc = new Aspose.Pdf.Document(htmlStamp, objLoadOptions);
        var firstPage = stampDoc.Pages[1];
        var contentBox = firstPage.CalculateContentBBox();
        objLoadOptions.PageInfo.Width = contentBox.Width;
        objLoadOptions.PageInfo.Height = contentBox.Height;
        using (var htmlStamp = new MemoryStream(Encoding.UTF8.GetBytes(html)))
          stampDoc = new Aspose.Pdf.Document(htmlStamp, objLoadOptions);
        if (stampDoc.Pages.Count > 0)
        {
          var mark = new Aspose.Pdf.PdfPageStamp(stampDoc.Pages[1]);
          mark.Background = false;
          return mark;
        }
        return null;
      }
      catch (Exception ex)
      {
        Logger.Error("Cannot create stamp from html", ex);
        throw new AppliedCodeException("Cannot create stamp from html");
      }
    }
    
    /// <summary>
    /// Добавить отметку о подписи на последнюю страницу документа без поиска символов-якорей.
    /// </summary>
    /// <param name="inputStream">Поток с входным документом.</param>
    /// <param name="stamp">Отметка о подписи.</param>
    /// <returns>Документ с отметкой о подписи.</returns>
    public virtual Stream AddSignatureMarkToDocumentWithoutAnchorSearch(Stream inputStream, Aspose.Pdf.PdfPageStamp stamp)
    {
      var document = new Aspose.Pdf.Document(inputStream);
      var lastPage = document.Pages[document.Pages.Count];
      var rectConsiderRotation = lastPage.GetPageRect(true);

      stamp.XIndent = (rectConsiderRotation.Width / 2) - (stamp.Width / 2);
      stamp.YIndent = BottomIndent;
      stamp.Background = false;

      return this.AddStampToDocumentPage(inputStream, lastPage.Number, stamp);
    }
    
    /// <summary>
    /// Добавить отметку на страницу документа.
    /// </summary>
    /// <param name="inputStream">Поток с входным документом.</param>
    /// <param name="pageNumber">Номер страницы документа, на которую нужно проставить отметку.</param>
    /// <param name="stamp">Отметка.</param>
    /// <returns>Страница документа с отметкой.</returns>
    public virtual Stream AddStampToDocumentPage(Stream inputStream, int pageNumber, Aspose.Pdf.PdfPageStamp stamp)
    {
      try
      {
        // Создание нового потока, в который будет записан документ с отметкой (во входной поток записывать нельзя).
        var outputStream = new MemoryStream();
        var document = new Aspose.Pdf.Document(inputStream);
        // Поднимаем версию и переполучаем документ из потока,
        // чтобы гарантировать читаемость штампа после вставки.
        using (var documentStream = this.GetUpgradedPdf(document))
        {
          document = new Aspose.Pdf.Document(documentStream);

          var documentPage = document.Pages[pageNumber];
          var rectConsiderRotation = documentPage.GetPageRect(true);
          if (stamp.Width > rectConsiderRotation.Width || stamp.Width > (rectConsiderRotation.Height - BottomIndent))
          {
            inputStream.CopyTo(outputStream);
          }
          else
          {
            documentPage.AddStamp(stamp);
            document.Save(outputStream);
          }
        }
        return outputStream;
      }
      catch (Exception ex)
      {
        Logger.Error("Cannot add stamp to document page", ex);
        throw new AppliedCodeException("Cannot add stamp to document page");
      }
      finally
      {
        inputStream.Close();
      }
    }
    
    /// <summary>
    /// Для документов версии ниже 1.4 поднять версию до 1.4 перед вставкой отметки.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <returns>PDF документ, сконвертированный до версии 1.4, или исходный, если версию поднимать не требовалось.</returns>
    /// <remarks>При вставке отметки в pdf версии ниже, чем 1.4, портятся шрифты в документе.
    /// В Adobe Reader такие документы либо не открываются совсем, либо отображаются некорректно.
    /// Для корректного отображения отметки pdf-документ будет сконвертирован до версии pdf 1.4.
    /// Документы в формате pdf/a не конвертируем, т.к. формат основан на версии pdf 1.4 и не требует конвертации.</remarks>
    public Stream GetUpgradedPdf(Aspose.Pdf.Document document)
    {
      if (!document.IsPdfaCompliant)
      {
        // Получить версию стандарта PDF из свойств документа. Достаточно первых двух чисел, разделённых точкой.
        var versionRegex = new Regex(@"^\d{1,2}\.\d{1,2}");
        var pdfVersionAsString = versionRegex.Match(document.Version).Value;
        var minCompatibleVersion = Version.Parse(MinCompatibleVersion);

        if (Version.TryParse(pdfVersionAsString, out Version version) && version < minCompatibleVersion)
          document.Convert(new Aspose.Pdf.PdfFormatConversionOptions(Aspose.Pdf.PdfFormat.v_1_4));
      }
      // Необходимо пересохранить документ в поток, чтобы изменение версии применилось до простановки отметки, а не после.
      var docStream = new MemoryStream();
      document.Save(docStream);
      return docStream;
    }
    
    /// <summary>
    /// Добавить отметку о подписи к документу согласно символу-якорю.
    /// </summary>
    /// <param name="inputStream">Поток с входным документом.</param>
    /// <param name="extension">Расширение файла.</param>
    /// <param name="htmlMark">Строка, содержащая html для отметки об ЭП.</param>
    /// <param name="anchorSymbol">Символ-якорь.</param>
    /// <param name="searchablePagesNumber">Количество страниц для поиска символа.</param>
    /// <returns>Поток с документом.</returns>
    /// <remarks>Поиск якорей доступен в документах с текстовым слоем. Если символов-якорей в документе нет, то отметка проставляется на последней странице.</remarks>
    public virtual Stream AddSignatureMark(Stream inputStream, string extension, string htmlMark, string anchorSymbol,
                                           int searchablePagesNumber)
    {
      try
      {
        var document = new Aspose.Pdf.Document(inputStream);
        var mark = this.CreateStampFromHtml(htmlMark);

        // Если в документе не предусмотрено наличие якорей (например, в картинке), то подпись - на последней странице.
        var anchorSymbolSearchNeeded = PdfConverter.PdfStamper.CheckIfExtensionIsSupportedForAnchorSearch(extension);
        if (!anchorSymbolSearchNeeded)
          return this.AddSignatureMarkToDocumentWithoutAnchorSearch(inputStream, mark);

        // Ограничение количества страниц, на которых будет искаться символ-якорь, применимо только к excel-файлам.
        var lastSearchablePage = this.GetLastSearchablePage(document.Pages.Count, searchablePagesNumber, extension);

        // Поиск символа производится постранично с конца документа.
        for (var pageNumber = document.Pages.Count; pageNumber > lastSearchablePage; pageNumber--)
        {
          var page = document.Pages[pageNumber];
          var lastAnchorEntry = this.GetLastAnchorEntry(page, anchorSymbol);
          if (lastAnchorEntry == null)
            continue;

          // Установить центры символа-якоря и отметки об ЭП на одной линии по горизонтали.
          mark.XIndent = lastAnchorEntry.Position.XIndent;
          mark.YIndent = lastAnchorEntry.Position.YIndent - (mark.Height / 2) + (lastAnchorEntry.Rectangle.Height / 2);

          return this.AddStampToDocumentPage(inputStream, page.Number, mark);
        }

        // Проставить отметку об ЭП на последней странице, если символ-якорь в документе не найден.
        return this.AddSignatureMarkToDocumentWithoutAnchorSearch(inputStream, mark);
      }
      catch (Exception ex)
      {
        Logger.Error("Cannot add stamp", ex);
        throw new AppliedCodeException("Cannot add stamp");
      }
    }
    
    #endregion
    
    #region Поиск якоря для вставки штампа
    
    /// <summary>
    /// Получить номер страницы с которой начинаем искать символ якоря.
    /// </summary>
    /// <param name="docPagesCount">Количество страниц документа.</param>
    /// <param name="searchablePagesNumber">Количество страниц для поиска символа.</param>
    /// <param name="extension">Расширение файла.</param>
    /// <returns>Номер страницы с которой начинаем искать символ якоря.</returns>
    /// <remarks>Для excel файлов ищем символ на последних searchablePagesNumber страницах, для всех остальных на всех страницах.</remarks>
    public virtual int GetLastSearchablePage(int docPagesCount, int searchablePagesNumber, string extension)
    {
      var excelFormats = new List<string>() { "xls", "xlsx", "ods" };
      
      return docPagesCount > searchablePagesNumber && excelFormats.Contains(extension) ?
        docPagesCount - searchablePagesNumber :
        0;
    }
    
    /// <summary>
    /// Преобразовать html для штампа в pdf.
    /// </summary>
    /// <param name="html">Строка html.</param>
    /// <returns>Штамп для вставки в pdf.</returns>
    public virtual Aspose.Pdf.PdfPageStamp CreateMarkFromHtml(string html)
    {
      try
      {
        Aspose.Pdf.HtmlLoadOptions objLoadOptions = new Aspose.Pdf.HtmlLoadOptions();
        objLoadOptions.PageInfo.Margin = new Aspose.Pdf.MarginInfo(0, 0, 0, 0);
        Aspose.Pdf.Document stampDoc;
        using (var htmlStamp = new MemoryStream(Encoding.UTF8.GetBytes(html)))
          stampDoc = new Aspose.Pdf.Document(htmlStamp, objLoadOptions);
        var firstPage = stampDoc.Pages[1];
        var contentBox = firstPage.CalculateContentBBox();
        objLoadOptions.PageInfo.Width = contentBox.Width;
        objLoadOptions.PageInfo.Height = contentBox.Height;
        using (var htmlStamp = new MemoryStream(Encoding.UTF8.GetBytes(html)))
          stampDoc = new Aspose.Pdf.Document(htmlStamp, objLoadOptions);
        if (stampDoc.Pages.Count > 0)
        {
          var mark = new Aspose.Pdf.PdfPageStamp(stampDoc.Pages[1]);
          mark.Background = false;
          return mark;
        }
        return null;
      }
      catch (Exception ex)
      {
        Logger.Error("Cannot transform html to pdf", ex);
        throw new AppliedCodeException("Cannot transform html to pdf");
      }
    }
    
    /// <summary>
    /// Определить по расширению файла, нужно ли искать в нём символы-якоря.
    /// </summary>
    /// <param name="extension">Расширение файла.</param>
    /// <returns>True/false.</returns>
    public static bool CheckIfExtensionIsSupportedForAnchorSearch(string extension)
    {
      var supportedFormatsForAnchorSearchList = new List<string>() { "pdf", "docx", "doc", "rtf", "xls", "xlsx",
        "odt", "ods", "txt" };
      
      return supportedFormatsForAnchorSearchList.Contains(extension.ToLower());
    }
    
    /// <summary>
    /// Получить последнее вхождение символа-якоря на странице.
    /// </summary>
    /// <param name="page">Страница.</param>
    /// <param name="anchor">Символ-якорь.</param>
    /// <returns>Фрагмент текста, являющийся последним вхождением. Null, если символ-якорь не найден.</returns>
    /// <remarks>Последним считается вхождение, находящееся ниже по странице.
    /// Если два вхождения располагаются на одном уровне - считается то, которое правее.</remarks>
    public virtual TextFragment GetLastAnchorEntry(Aspose.Pdf.Page page, string anchor)
    {
      var absorber = new TextFragmentAbsorber(anchor);
      page.Accept(absorber);
      if (absorber.TextFragments.Count == 0)
        return null;

      // Найти последнее вхождение символа-якоря на странице.
      // Условное самое первое вхождение будет иметь координаты левого верхнего угла.
      // https://forum.aspose.com/t/textfragment-at-top-of-page/64774.
      // Ось X - горизонтальная.
      // Ось Y - вертикальная.
      // Начало координат - левый нижний угол.
      var lastEntry = new TextFragment();
      var rectConsiderRotation = page.GetPageRect(true);
      lastEntry.Position.XIndent = 0;
      lastEntry.Position.YIndent = rectConsiderRotation.Height;
      foreach (TextFragment textFragment in absorber.TextFragments)
      {
        if (textFragment.Position.YIndent < lastEntry.Position.YIndent ||
            textFragment.Position.YIndent == lastEntry.Position.YIndent &&
            textFragment.Position.XIndent > lastEntry.Position.XIndent)
          lastEntry = textFragment;
      }

      return lastEntry;
    }
    
    #endregion
    
    #region Методы для заказной разработки
    
    /// <summary>
    /// Получить документ с отметкой на всех страницах.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="stamp">Отметка.</param>
    /// <param name="needUpgradePdfVersion">Признак того, что нужно повышать версию PDF перед простановкой отметки.</param>
    /// <returns>Документ с проставленной отметкой.</returns>
    /// <remarks>Координаты места простановки берутся из свойств отметки.</remarks>
    public virtual Stream GetPdfDocumentWithStamp(Aspose.Pdf.Document document, Aspose.Pdf.PdfPageStamp stamp, bool needUpgradePdfVersion)
    {
      // Поднимаем версию и переполучаем документ из потока,
      // чтобы гарантировать читаемость отметки после вставки.
      var upgradedDocumentStream = new MemoryStream();
      if (needUpgradePdfVersion)
      {
        upgradedDocumentStream = (MemoryStream)this.GetUpgradedPdf(document);
        document = new Aspose.Pdf.Document(upgradedDocumentStream);
      }

      foreach (Aspose.Pdf.Page documentPage in document.Pages)
        documentPage.AddStamp(stamp);

      var resultStream = new MemoryStream();
      document.Save(resultStream);

      // Закрытие входного потока, чтобы он не висел в памяти.
      upgradedDocumentStream.Close();

      return resultStream;
    }

    /// <summary>
    /// Получить документ с отметкой на заданных страницах.
    /// </summary>
    /// <param name="document">Документ.</param>
    /// <param name="stamp">Отметка.</param>
    /// <param name="pageNumbers">Массив номеров страниц, на которые нужно поставить отметку.</param>
    /// <param name="needUpgradePdfVersion">Признак того, что нужно повышать версию PDF перед простановкой отметки.</param>
    /// <returns>Документ с проставленной отметкой.</returns>
    /// <remarks>Координаты места простановки берутся из свойств отметки.</remarks>
    public virtual Stream GetPdfDocumentWithStamp(Aspose.Pdf.Document document, Aspose.Pdf.PdfPageStamp stamp, int[] pageNumbers, bool needUpgradePdfVersion)
    {
      // Поднимаем версию и переполучаем документ из потока,
      // чтобы гарантировать читаемость отметки после вставки.
      var upgradedDocumentStream = new MemoryStream();
      if (needUpgradePdfVersion)
      {
        upgradedDocumentStream = (MemoryStream)this.GetUpgradedPdf(document);
        document = new Aspose.Pdf.Document(upgradedDocumentStream);
      }

      foreach (var pageNumber in pageNumbers)
      {
        if (document.Pages.Count >= pageNumber)
        {
          var documentPage = document.Pages[pageNumber];
          documentPage.AddStamp(stamp);
        }
      }

      var resultStream = new MemoryStream();
      document.Save(resultStream);

      // Закрытие входного потока, чтобы он не висел в памяти.
      upgradedDocumentStream.Close();

      return resultStream;
    }
    
    /// <summary>
    /// Добавить штамп к документу по координатам.
    /// </summary>
    /// <param name="inputStream">Поток с входным документом.</param>
    /// <param name="htmlMark">Строка, содержащая html для штампа.</param>
    /// <param name="rightIndentInCm">Отступ с правого края, в см.</param>
    /// <param name="bottomIndentInCm">Отступ с нижнего края, в см.</param>
    /// <returns>Поток с документом.</returns>
    /// <remarks>Штамп будет добавлен на последнюю страницу документа.</remarks>
    public virtual Stream AddStampToLastPage(Stream inputStream, string htmlMark, double rightIndentInCm, double bottomIndentInCm)
    {
      try
      {
        var document = new Aspose.Pdf.Document(inputStream);
        var mark = this.CreateMarkFromHtml(htmlMark);
        mark.Background = false;

        // Установить координаты отметки.
        var lastPage = document.Pages[document.Pages.Count];
        var rectConsiderRotation = lastPage.GetPageRect(true);
        mark.XIndent = rectConsiderRotation.Width - (rightIndentInCm * DotsPerCm) - mark.Width;
        mark.YIndent = bottomIndentInCm * DotsPerCm;

        return this.AddStampToDocumentPage(inputStream, lastPage.Number, mark);
      }
      catch (Exception ex)
      {
        Logger.Error("Cannot add stamp", ex);
        throw new AppliedCodeException("Cannot add stamp");
      }
    }
    
    #endregion

  }
  
  /// <summary>
  /// Масштабированный размер страницы.
  /// </summary>
  public class ScaledPageSize
  {
    /// <summary>
    /// Ширина.
    /// </summary>
    /// <value>По умолчанию пустое значение. Заполняется при расчете размеров страницы.</value>
    public double Width { get; set; }
    
    /// <summary>
    /// Высота.
    /// </summary>
    /// <value>По умолчанию пустое значение. Заполняется при расчете размеров страницы.</value>
    public double Height { get; set; }
  }
}