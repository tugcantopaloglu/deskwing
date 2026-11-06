import XCTest
import UIKit
@testable import DeskWing

final class DisplayInputTests: XCTestCase {
    func testAspectFitMappingPreservesAllEdgesAndRejectsLetterboxes() {
        let viewport=CGSize(width:1000,height:1000), image=CGSize(width:1600,height:1000)
        XCTAssertEqual(DisplayInputPolicy.point(CGPoint(x:0,y:187.5),viewport:viewport,image:image),DisplayInputPoint(x:0,y:0))
        XCTAssertEqual(DisplayInputPolicy.point(CGPoint(x:1000,y:812.5),viewport:viewport,image:image),DisplayInputPoint(x:1,y:1))
        XCTAssertEqual(DisplayInputPolicy.point(CGPoint(x:500,y:500),viewport:viewport,image:image),DisplayInputPoint(x:0.5,y:0.5))
        XCTAssertNil(DisplayInputPolicy.point(CGPoint(x:500,y:187),viewport:viewport,image:image))
        XCTAssertNil(DisplayInputPolicy.point(CGPoint(x:1001,y:500),viewport:viewport,image:image))
        XCTAssertNil(DisplayInputPolicy.point(CGPoint(x:CGFloat.nan,y:500),viewport:viewport,image:image))
        XCTAssertNil(DisplayInputPolicy.point(.zero,viewport:.zero,image:image))
    }
    func testPortraitGeometryIsNotStretchedIntoLandscapeImage() {
        let viewport=CGSize(width:600,height:1000), image=CGSize(width:1000,height:1000)
        XCTAssertNil(DisplayInputPolicy.point(CGPoint(x:300,y:199),viewport:viewport,image:image))
        XCTAssertEqual(DisplayInputPolicy.point(CGPoint(x:600,y:800),viewport:viewport,image:image),DisplayInputPoint(x:1,y:1))
    }
    func testExplicitModesRejectPalmsAndViewingNeverAdmitsInput() {
        XCTAssertFalse(DisplayInputPolicy.accepts(mode:.view,pencil:true,direct:false))
        XCTAssertFalse(DisplayInputPolicy.accepts(mode:.view,pencil:false,direct:true))
        XCTAssertTrue(DisplayInputPolicy.accepts(mode:.pen,pencil:true,direct:false))
        XCTAssertFalse(DisplayInputPolicy.accepts(mode:.pen,pencil:false,direct:true))
        XCTAssertTrue(DisplayInputPolicy.accepts(mode:.touch,pencil:false,direct:true))
        XCTAssertFalse(DisplayInputPolicy.accepts(mode:.touch,pencil:true,direct:false))
    }
    func testPencilPressureTiltAndReleaseAreBounded() throws {
        let point=DisplayInputPoint(x:0.4,y:0.7)
        let upright=try XCTUnwrap(DisplayInputPolicy.sample(phase:"down",point:point,force:2,maximumForce:4,altitude:.pi/2,azimuth:0,mode:.pen))
        XCTAssertEqual(upright.pressure,0.5); XCTAssertEqual(upright.tiltX,0,accuracy:0.001); XCTAssertEqual(upright.tiltY,0,accuracy:0.001)
        let tilted=try XCTUnwrap(DisplayInputPolicy.sample(phase:"move",point:point,force:8,maximumForce:4,altitude:.pi/4,azimuth:0,mode:.pen))
        XCTAssertEqual(tilted.pressure,1); XCTAssertEqual(tilted.tiltX,45,accuracy:0.001)
        let up=try XCTUnwrap(DisplayInputPolicy.sample(phase:"up",point:point,force:8,maximumForce:4,altitude:.pi/4,azimuth:0,mode:.pen))
        XCTAssertEqual(up.pressure,0)
        XCTAssertNil(DisplayInputPolicy.sample(phase:"move",point:point,force:.nan,maximumForce:4,altitude:1,azimuth:0,mode:.pen))
        XCTAssertNil(DisplayInputPolicy.sample(phase:"move",point:DisplayInputPoint(x:1.1,y:0),force:1,maximumForce:4,altitude:1,azimuth:0,mode:.pen))
    }
    func testTouchIsOneFingerPenWithNoInventedPressureOrTilt() throws {
        let sample=try XCTUnwrap(DisplayInputPolicy.sample(phase:"down",point:DisplayInputPoint(x:0,y:1),force:99,maximumForce:1,altitude:0,azimuth:1,mode:.touch))
        XCTAssertEqual(sample.pressure,0.5); XCTAssertEqual(sample.tiltX,0); XCTAssertEqual(sample.tiltY,0)
        XCTAssertEqual(sample.json["barrel"] as? Bool,false); XCTAssertEqual(sample.json["eraser"] as? Bool,false)
    }
    func testQueueCoalescesMovesButNeverDropsContactBoundaries() {
        var queue=DisplayInputQueue()
        func sample(_ phase:String,_ x:Double=0) -> DisplayInputSample { DisplayInputSample(phase:phase,point:DisplayInputPoint(x:x,y:0),pressure:0.5,tiltX:0,tiltY:0) }
        XCTAssertTrue(queue.offer(sample("down")))
        for index in 0..<1000 { XCTAssertTrue(queue.offer(sample("move",Double(index)/1000))) }
        XCTAssertEqual(queue.samples.count,32)
        XCTAssertTrue(queue.offer(sample("up"))); XCTAssertTrue(queue.offer(sample("leave")))
        let first=queue.take(32), last=queue.take(32)
        XCTAssertEqual(first.first?.phase,"down"); XCTAssertEqual(first.last?.point.x,0.999)
        XCTAssertEqual(last.map(\.phase),["up","leave"]); XCTAssertTrue(queue.isEmpty)
    }
    func testQueueRefusesUnboundedBoundaryBacklogAndCanBeRetired() {
        var queue=DisplayInputQueue()
        let sample=DisplayInputSample(phase:"down",point:DisplayInputPoint(x:0,y:0),pressure:0.5,tiltX:0,tiltY:0)
        for _ in 0..<64 { XCTAssertTrue(queue.offer(sample)) }
        XCTAssertFalse(queue.offer(sample)); XCTAssertEqual(queue.samples.count,64)
        queue.reset(); XCTAssertTrue(queue.isEmpty)
    }
    func testInputChannelIsExactAndNotPartOfTheJavaScriptProxy() {
        XCTAssertTrue(DisplayInputPolicy.allowedRequest(DisplayInputPolicy.path,method:"POST",body:["operation":"begin"]))
        XCTAssertTrue(DisplayInputPolicy.allowedRequest(DisplayInputPolicy.path,method:"POST",body:["operation":"cancel"]))
        XCTAssertFalse(DisplayInputPolicy.allowedRequest(DisplayInputPolicy.path+"/copy",method:"POST",body:["operation":"begin"]))
        XCTAssertFalse(DisplayInputPolicy.allowedRequest(DisplayInputPolicy.path,method:"GET",body:["operation":"begin"]))
        XCTAssertFalse(DisplayInputPolicy.allowedRequest(DisplayInputPolicy.path,method:"POST",body:["operation":"delete"]))
        XCTAssertFalse(HostPolicy.allowed(DisplayInputPolicy.path,method:"POST"))
    }
    func testLeaseAndNegotiationValuesRejectBooleansAndMalformedTokens() {
        XCTAssertEqual(DisplayInputPolicy.lease(String(repeating:"A",count:48)),String(repeating:"A",count:48))
        XCTAssertNil(DisplayInputPolicy.lease(String(repeating:"G",count:48)))
        XCTAssertNil(DisplayInputPolicy.lease(String(repeating:"A",count:47)))
        XCTAssertNil(DisplayInputPolicy.integer(true)); XCTAssertNil(DisplayInputPolicy.integer(1.5)); XCTAssertEqual(DisplayInputPolicy.integer(32),32)
    }
    func testQualityOptionsKeepLegacyDefaultsAndRejectAmbiguousValues() throws {
        XCTAssertEqual(try DisplayQualityOptions.parse(["session":"s","refreshMode":"60"]),DisplayQualityOptions(preset:"balanced",adaptive:true))
        XCTAssertEqual(try DisplayQualityOptions.parse(["session":"s","refreshMode":"60","qualityPreset":"battery-saver","adaptiveQuality":false]),DisplayQualityOptions(preset:"battery-saver",adaptive:false))
        XCTAssertThrowsError(try DisplayQualityOptions.parse(["session":"s","refreshMode":"60","qualityPreset":"fastest"]))
        XCTAssertThrowsError(try DisplayQualityOptions.parse(["session":"s","refreshMode":"60","adaptiveQuality":1]))
        XCTAssertThrowsError(try DisplayQualityOptions.parse(["session":"s","refreshMode":"60","unexpected":true]))
    }
}
